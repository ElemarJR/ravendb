using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Raven.Client;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Replication.Messages;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Binary;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Voron;
using Voron.Data.Tables;
using Voron.Exceptions;
using System.Linq;
using Raven.Client.Server.Versioning;

namespace Raven.Server.Documents
{
    public unsafe class DocumentPutAction
    {
        private readonly DocumentsStorage _documentsStorage;
        private readonly DocumentDatabase _documentDatabase;

        public DocumentPutAction(DocumentsStorage documentsStorage, DocumentDatabase documentDatabase)
        {
            _documentsStorage = documentsStorage;
            _documentDatabase = documentDatabase;
        }

        public DocumentsStorage.PutOperationResults PutDocument(DocumentsOperationContext context, string id, long? expectedEtag,
            BlittableJsonReaderObject document,
            long? lastModifiedTicks = null,
            ChangeVectorEntry[] changeVector = null,
            DocumentFlags flags = DocumentFlags.None,
            NonPersistentDocumentFlags nonPersistentFlags = NonPersistentDocumentFlags.None)
        {
            if (context.Transaction == null)
            {
                ThrowRequiresTransaction();
                return default(DocumentsStorage.PutOperationResults); // never hit
            }

#if DEBUG
            var documentDebugHash = document.DebugHash;
            document.BlittableValidation();
            BlittableJsonReaderObject.AssertNoModifications(document, id, assertChildren: true);
            AssertMetadataWasFiltered(document);
            if ((nonPersistentFlags & NonPersistentDocumentFlags.ResolveAttachmentsConflict) != NonPersistentDocumentFlags.ResolveAttachmentsConflict)
                AttachmentsStorage.AssertAttachments(document, flags);
#endif

            var collectionName = _documentsStorage.ExtractCollectionName(context, id, document);
            var newEtag = _documentsStorage.GenerateNextEtag();

            var modifiedTicks = lastModifiedTicks ?? _documentDatabase.Time.GetUtcNow().Ticks;

            var table = context.Transaction.InnerTransaction.OpenTable(DocumentsStorage.DocsSchema, collectionName.GetTableName(CollectionTableType.Documents));

            id = BuildDocumentId(context, id, table, newEtag, out bool knownNewId);
            using (DocumentIdWorker.GetLowerIdSliceAndStorageKey(context, id, out Slice lowerId, out Slice idPtr))
            {
                var oldValue = default(TableValueReader);
                if (knownNewId == false)
                {
                    // delete a tombstone if it exists, if it known that it is a new ID, no need, so we can skip it
                    DeleteTombstoneIfNeeded(context, collectionName, lowerId.Content.Ptr, lowerId.Size);

                    table.ReadByKey(lowerId, out oldValue);
                }

                BlittableJsonReaderObject oldDoc = null;
                if (oldValue.Pointer == null)
                {
                    if (expectedEtag != null && expectedEtag != 0)
                    {
                        ThrowConcurrentExceptionOnMissingDoc(id, expectedEtag.Value);
                    }
                }
                else
                {
                    if (expectedEtag != null)
                    {
                        var oldEtag = DocumentsStorage.TableValueToEtag(1, ref oldValue);
                        if (oldEtag != expectedEtag)
                            ThrowConcurrentException(id, expectedEtag, oldEtag);
                    }

                    oldDoc = new BlittableJsonReaderObject(oldValue.Read((int)DocumentsStorage.DocumentsTable.Data, out int oldSize), oldSize, context);
                    var oldCollectionName = _documentsStorage.ExtractCollectionName(context, id, oldDoc);
                    if (oldCollectionName != collectionName)
                        ThrowInvalidCollectionNameChange(id, oldCollectionName, collectionName);

                    var oldFlags = *(DocumentFlags*)oldValue.Read((int)DocumentsStorage.DocumentsTable.Flags, out int size);

                    if ((nonPersistentFlags & NonPersistentDocumentFlags.ByAttachmentUpdate) != NonPersistentDocumentFlags.ByAttachmentUpdate &&
                        (nonPersistentFlags & NonPersistentDocumentFlags.FromReplication) != NonPersistentDocumentFlags.FromReplication)
                    {
                        if ((oldFlags & DocumentFlags.HasAttachments) == DocumentFlags.HasAttachments)
                        {
                            flags |= DocumentFlags.HasAttachments;
                        }
                    }
                }

                var result = BuildChangeVectorAndResolveConflicts(context, id, lowerId, newEtag, document, changeVector, expectedEtag, flags, oldValue);
                changeVector = result.ChangeVector;
                nonPersistentFlags |= result.NonPersistentFlags;

                if (collectionName.IsSystem == false &&
                    (flags & DocumentFlags.Artificial) != DocumentFlags.Artificial)
                {
                    if (ShouldRecreateAttachments(context, lowerId, oldDoc, document, ref flags, nonPersistentFlags))
                    {
#if DEBUG
                        if (document.DebugHash != documentDebugHash)
                        {
                            throw new InvalidDataException("The incoming document " + id + " has changed _during_ the put process, " +
                                                           "this is likely because you are trying to save a document that is already stored and was moved");
                        }
#endif
                        document = context.ReadObject(document, id, BlittableJsonDocumentBuilder.UsageMode.ToDisk);
#if DEBUG
                        documentDebugHash = document.DebugHash;
                        document.BlittableValidation();
                        BlittableJsonReaderObject.AssertNoModifications(document, id, assertChildren: true);
                        AssertMetadataWasFiltered(document);
                        AttachmentsStorage.AssertAttachments(document, flags);
#endif
                    }

                    if (_documentDatabase.DocumentsStorage.VersioningStorage.Configuration != null &&
                        (nonPersistentFlags & NonPersistentDocumentFlags.FromReplication) != NonPersistentDocumentFlags.FromReplication)
                    {
                        if (_documentDatabase.DocumentsStorage.VersioningStorage.ShouldVersionDocument(collectionName, nonPersistentFlags, oldDoc, document,
                            ref flags, out VersioningCollectionConfiguration configuration))
                        {
                            _documentDatabase.DocumentsStorage.VersioningStorage.Put(context, id, document, flags, nonPersistentFlags, changeVector, modifiedTicks, configuration);
                        }
                    }
                }

                fixed (ChangeVectorEntry* pChangeVector = changeVector)
                {
                    using (table.Allocate(out TableValueBuilder tvb))
                    {
                        tvb.Add(lowerId);
                        tvb.Add(Bits.SwapBytes(newEtag));
                        tvb.Add(idPtr);
                        tvb.Add(document.BasePointer, document.Size);
                        tvb.Add((byte*)pChangeVector, sizeof(ChangeVectorEntry) * changeVector.Length);
                        tvb.Add(modifiedTicks);
                        tvb.Add((int)flags);
                        tvb.Add(context.GetTransactionMarker());

                        if (oldValue.Pointer == null)
                        {
                            table.Insert(tvb);
                        }
                        else
                        {
                            table.Update(oldValue.Id, tvb);
                        }
                    }
                }

                if (collectionName.IsSystem == false)
                {
                    _documentDatabase.ExpiredDocumentsCleaner?.Put(context, lowerId, document);
                }

                _documentDatabase.DocumentsStorage.SetDatabaseChangeVector(context, changeVector);
                _documentDatabase.Metrics.DocPutsPerSecond.MarkSingleThreaded(1);
                _documentDatabase.Metrics.BytesPutsPerSecond.MarkSingleThreaded(document.Size);

                context.Transaction.AddAfterCommitNotification(new DocumentChange
                {
                    Etag = newEtag,
                    CollectionName = collectionName.Name,
                    Id = id,
                    Type = DocumentChangeTypes.Put,
                    IsSystemDocument = collectionName.IsSystem,
                });

#if DEBUG
                if (document.DebugHash != documentDebugHash)
                {
                    throw new InvalidDataException("The incoming document " + id + " has changed _during_ the put process, " +
                                                   "this is likely because you are trying to save a document that is already stored and was moved");
                }
                document.BlittableValidation();
                BlittableJsonReaderObject.AssertNoModifications(document, id, assertChildren: true);
                AssertMetadataWasFiltered(document);
                AttachmentsStorage.AssertAttachments(document, flags);
#endif

                return new DocumentsStorage.PutOperationResults
                {
                    Etag = newEtag,
                    Id = id,
                    Collection = collectionName,
                    ChangeVector = changeVector,
                    Flags = flags,
                    LastModified = new DateTime(modifiedTicks)
                };
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (ChangeVectorEntry[] ChangeVector, NonPersistentDocumentFlags NonPersistentFlags) BuildChangeVectorAndResolveConflicts(
            DocumentsOperationContext context, string id, Slice lowerId, long newEtag, 
            BlittableJsonReaderObject document, ChangeVectorEntry[] changeVector, long? expectedEtag, DocumentFlags flags, TableValueReader oldValue)
        {
            var nonPersistentFlags = NonPersistentDocumentFlags.None;
            var fromReplication = (flags & DocumentFlags.FromReplication) == DocumentFlags.FromReplication;

            if (_documentsStorage.ConflictsStorage.ConflictsCount != 0)
            {
                // Since this document resolve the conflict we don't need to alter the change vector.
                // This way we avoid another replication back to the source
                if (_documentsStorage.ConflictsStorage.ShouldThrowConcurrencyExceptionOnConflict(context, lowerId, expectedEtag, out var currentMaxConflictEtag))
                {
                    _documentsStorage.ConflictsStorage.ThrowConcurrencyExceptionOnConflict(expectedEtag, currentMaxConflictEtag);
                }

                if (fromReplication)
                {
                    nonPersistentFlags = _documentsStorage.ConflictsStorage.DeleteConflictsFor(context, id, document).NonPersistentFlags;
                }
                else
                {
                    var result = _documentsStorage.ConflictsStorage.MergeConflictChangeVectorIfNeededAndDeleteConflicts(changeVector, context, id, newEtag, document);
                    changeVector = result.ChangeVector;
                    nonPersistentFlags = result.NonPersistentFlags;
                }
            }

            if (changeVector != null)
               return (changeVector, nonPersistentFlags);

            ChangeVectorEntry[] oldChangeVector;
            if (fromReplication == false)
            {
                oldChangeVector = _documentsStorage.GetDatabaseChangeVector(context);
            }
            else
            {
                oldChangeVector = oldValue.Pointer != null ? DocumentsStorage.TableValueToChangeVector(ref oldValue, (int)DocumentsStorage.DocumentsTable.ChangeVector) : null;
            }
            changeVector = SetDocumentChangeVectorForLocalChange(context, lowerId, oldChangeVector, newEtag);
            return (changeVector, nonPersistentFlags);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private string BuildDocumentId(DocumentsOperationContext context, string id, Table table, long newEtag, out bool knownNewId)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                knownNewId = true;
                id = Guid.NewGuid().ToString();
            }
            else
            {
                // We use if instead of switch so the JIT will better inline this method
                var lastChar = id[id.Length - 1];
                if (lastChar == '/')
                {
                    ThrowInvalidDocumentId(id);
                }

                if (lastChar == '|')
                {
                    knownNewId = true;
                    id = _documentsStorage.Identities.AppendNumericValueToId(id, newEtag);
                }
                else
                {
                    knownNewId = false;
                }
            }

            // Intentionally have just one return statement here for better inlining
            return id;
        }

        private static void ThrowInvalidDocumentId(string id)
        {
            throw new NotSupportedException("Document ids cannot end with '/', but was called with " + id +
                                            ". Identities are only generated for external requests, not calls to PutDocument and such.");
        }

        private void RecreateAttachments(DocumentsOperationContext context, Slice lowerId, BlittableJsonReaderObject document, 
            BlittableJsonReaderObject metadata, ref DocumentFlags flags)
        {
            var actualAttachments = _documentsStorage.AttachmentsStorage.GetAttachmentsMetadataForDocument(context, lowerId);
            if (actualAttachments.Count == 0)
            {
                if (metadata != null)
                {
                    metadata.Modifications = new DynamicJsonValue(metadata);
                    metadata.Modifications.Remove(Constants.Documents.Metadata.Attachments);
                    document.Modifications = new DynamicJsonValue(document)
                    {
                        [Constants.Documents.Metadata.Key] = metadata
                    };
                }

                flags &= ~DocumentFlags.HasAttachments;
                return;
            }

            flags |= DocumentFlags.HasAttachments;
            if (metadata == null)
            {
                document.Modifications = new DynamicJsonValue(document)
                {
                    [Constants.Documents.Metadata.Key] = new DynamicJsonValue
                    {
                        [Constants.Documents.Metadata.Attachments] = actualAttachments
                    }
                };
            }
            else
            {
                metadata.Modifications = new DynamicJsonValue(metadata)
                {
                    [Constants.Documents.Metadata.Attachments] = actualAttachments
                };
                document.Modifications = new DynamicJsonValue(document)
                {
                    [Constants.Documents.Metadata.Key] = metadata
                };
            }
        }

        private bool ShouldRecreateAttachments(DocumentsOperationContext context, Slice lowerId, BlittableJsonReaderObject oldDoc, 
            BlittableJsonReaderObject document, ref DocumentFlags flags, NonPersistentDocumentFlags nonPersistentFlags)
        {
            if ((nonPersistentFlags & NonPersistentDocumentFlags.ResolveAttachmentsConflict) == NonPersistentDocumentFlags.ResolveAttachmentsConflict)
            {
                document.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata);
                RecreateAttachments(context, lowerId, document, metadata, ref flags);
                return true;
            }

            if ((flags & DocumentFlags.HasAttachments) == DocumentFlags.HasAttachments &&
                (nonPersistentFlags & NonPersistentDocumentFlags.ByAttachmentUpdate) != NonPersistentDocumentFlags.ByAttachmentUpdate &&
                (nonPersistentFlags & NonPersistentDocumentFlags.FromReplication) != NonPersistentDocumentFlags.FromReplication)
            {
                if (oldDoc != null && 
                    oldDoc.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject oldMetadata) &&
                    oldMetadata.TryGet(Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray oldAttachments))
                {
                    // Make sure the user did not changed the value of @attachments in the @metadata
                    // In most cases it won't be changed so we can use this value 
                    // instead of recreating the document's blitable from scratch
                    if (document.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata) == false ||
                        metadata.TryGet(Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray attachments) == false ||
                        attachments.Equals(oldAttachments) == false)
                    {
                        RecreateAttachments(context, lowerId, document, metadata, ref flags);
                        return true;
                    }
                }
            }

            return false;
        }

        public static void ThrowRequiresTransaction([CallerMemberName]string caller = null)
        {
            // ReSharper disable once NotResolvedInText
            throw new ArgumentException("Context must be set with a valid transaction before calling " + caller, "context");
        }

        private static void ThrowConcurrentExceptionOnMissingDoc(string id, long expectedEtag)
        {
            throw new ConcurrencyException(
                $"Document {id} does not exist, but Put was called with etag {expectedEtag}. Optimistic concurrency violation, transaction will be aborted.")
            {
                ExpectedETag = expectedEtag
            };
        }

        private static void ThrowInvalidCollectionNameChange(string id, CollectionName oldCollectionName, CollectionName collectionName)
        {
            throw new InvalidOperationException(
                $"Changing '{id}' from '{oldCollectionName.Name}' to '{collectionName.Name}' via update is not supported.{System.Environment.NewLine}" +
                $"Delete it and recreate the document {id}.");
        }

        private static void ThrowConcurrentException(string id, long? expectedEtag, long oldEtag)
        {
            throw new ConcurrencyException(
                $"Document {id} has etag {oldEtag}, but Put was called with etag {expectedEtag}. Optimistic concurrency violation, transaction will be aborted.")
            {
                ActualETag = oldEtag,
                ExpectedETag = expectedEtag ?? -1
            };
        }

        private static void DeleteTombstoneIfNeeded(DocumentsOperationContext context, CollectionName collectionName, byte* lowerId, int lowerSize)
        {
            var tombstoneTable = context.Transaction.InnerTransaction.OpenTable(DocumentsStorage.TombstonesSchema, collectionName.GetTableName(CollectionTableType.Tombstones));
            using (Slice.External(context.Allocator, lowerId, lowerSize, out Slice id))
            {
                tombstoneTable.DeleteByKey(id);
            }
        }

        private ChangeVectorEntry[] SetDocumentChangeVectorForLocalChange(
            DocumentsOperationContext context, Slice lowerId,
            ChangeVectorEntry[] oldChangeVector, long newEtag)
        {
            if (oldChangeVector != null)
                return ChangeVectorUtils.UpdateChangeVectorWithNewEtag(_documentsStorage.Environment.DbId, newEtag, oldChangeVector);

            return _documentsStorage.ConflictsStorage.GetMergedConflictChangeVectorsAndDeleteConflicts(context, lowerId, newEtag);
        }

        [Conditional("DEBUG")]
        public static void AssertMetadataWasFiltered(BlittableJsonReaderObject data)
        {
            if (data.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata) == false)
                return;

            var names = metadata.GetPropertyNames();
            if (names.Contains(Constants.Documents.Metadata.Id, StringComparer.OrdinalIgnoreCase) ||
                names.Contains(Constants.Documents.Metadata.Etag, StringComparer.OrdinalIgnoreCase) ||
                names.Contains(Constants.Documents.Metadata.LastModified, StringComparer.OrdinalIgnoreCase) ||
                names.Contains(Constants.Documents.Metadata.IndexScore, StringComparer.OrdinalIgnoreCase) ||
                names.Contains(Constants.Documents.Metadata.ChangeVector, StringComparer.OrdinalIgnoreCase) ||
                names.Contains(Constants.Documents.Metadata.Flags, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Document's metadata should filter properties on before put to storage." + System.Environment.NewLine + data);
            }
        }
    }
}