﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Sparrow;
using Sparrow.Logging;
using Sparrow.Platform;
using Sparrow.Utils;
using Voron.Exceptions;
using Voron.Global;
using Voron.Impl;
using Voron.Impl.FileHeaders;
using Voron.Impl.Journal;
using Voron.Impl.Paging;
using Voron.Platform.Posix;
using Voron.Platform.Win32;
using Voron.Util.Settings;

namespace Voron
{
    public abstract class StorageEnvironmentOptions : IDisposable
    {
        public const string RecyclableJournalFileNamePrefix = "recyclable-journal";

        private ExceptionDispatchInfo _catastrophicFailure;
        private readonly CatastrophicFailureNotification _catastrophicFailureNotification;

        public VoronPathSetting TempPath { get; }

        public IoMetrics IoMetrics { get; set; }

        public bool GenerateNewDatabaseId { get; set; }

        public event EventHandler<RecoveryErrorEventArgs> OnRecoveryError;
        public event EventHandler<NonDurabilitySupportEventArgs> OnNonDurableFileSystemError;
        private long _reuseCounter;
        public abstract override string ToString();

        private bool _forceUsing32BitsPager;
        public bool ForceUsing32BitsPager
        {
            get => _forceUsing32BitsPager;
            set
            {
                _forceUsing32BitsPager = value;
                MaxLogFileSize = (value ? 32 : 256) * Constants.Size.Megabyte;
                MaxScratchBufferSize = (value ? 32 : 256) * Constants.Size.Megabyte;
                MaxNumberOfPagesInJournalBeforeFlush = (value ? 4 : 32) * Constants.Size.Megabyte / Constants.Storage.PageSize;
            }
        }

        public void InvokeRecoveryError(object sender, string message, Exception e)
        {
            var handler = OnRecoveryError;
            if (handler == null)
            {
                throw new InvalidDataException(message + Environment.NewLine +
                                               "An exception has been thrown because there isn't a listener to the OnRecoveryError event on the storage options.",
                    e);
            }

            handler(this, new RecoveryErrorEventArgs(message, e));
        }

        public void InvokeNonDurableFileSystemError(object sender, string message, Exception e, string details)
        {
            var handler = OnNonDurableFileSystemError;
            if (handler == null)
            {
                throw new InvalidDataException(message + Environment.NewLine +
                                               "An exception has been thrown because there isn't a listener to the OnNonDurableFileSystemError event on the storage options.",
                    e);
            }

            handler(this, new NonDurabilitySupportEventArgs(message, e, details));
        }

        public long? InitialFileSize { get; set; }

        public long MaxLogFileSize
        {
            get { return _maxLogFileSize; }
            set
            {
                if (value < _initialLogFileSize)
                    InitialLogFileSize = value;
                _maxLogFileSize = value;
            }
        }

        public long InitialLogFileSize
        {
            get { return _initialLogFileSize; }
            set
            {
                if (value > MaxLogFileSize)
                    MaxLogFileSize = value;
                if (value <= 0)
                    ThrowInitialLogFileSizeOutOfRange();
                _initialLogFileSize = value;
            }
        }

        private static void ThrowInitialLogFileSizeOutOfRange()
        {
            throw new ArgumentOutOfRangeException("InitialLogFileSize", "The initial log for the Voron must be above zero");
        }

        public int PageSize => Constants.Storage.PageSize;

        // if set to a non zero value, will check that the expected schema is there
        public int SchemaVersion { get; set; }

        public UpgraderDelegate SchemaUpgrader { get; set; }

        public long MaxScratchBufferSize { get; set; }

        public bool OwnsPagers { get; set; }

        public bool ManualFlushing { get; set; }

        public bool IncrementalBackupEnabled { get; set; }

        public abstract AbstractPager DataPager { get; }

        public long MaxNumberOfPagesInJournalBeforeFlush { get; set; }

        public int IdleFlushTimeout { get; set; }

        public long? MaxStorageSize { get; set; }

        public abstract VoronPathSetting BasePath { get; }

        /// <summary>
        /// This mode is used in the Voron recovery tool and is not intended to be set otherwise.
        /// </summary>
        internal bool CopyOnWriteMode { get; set; }

        public abstract IJournalWriter CreateJournalWriter(long journalNumber, long journalSize);

        public abstract VoronPathSetting GetJournalPath(long journalNumber);

        protected bool Disposed;
        private long _initialLogFileSize;
        private long _maxLogFileSize;

        public Func<string, bool> ShouldUseKeyPrefix { get; set; }

        public string DocumentDatabaseName { get; set; }
        public ConcurrentQueue<string> InitLogQueue { get; set; }

        protected StorageEnvironmentOptions(VoronPathSetting tempPath, IoChangesNotifications ioChangesNotifications, CatastrophicFailureNotification catastrophicFailureNotification)
        {
            SafePosixOpenFlags = SafePosixOpenFlags | DefaultPosixFlags;

            DisposeWaitTime = TimeSpan.FromSeconds(15);

            TempPath = tempPath;

            ShouldUseKeyPrefix = name => false;

            MaxLogFileSize = ((sizeof(int) == IntPtr.Size ? 32 : 256) * Constants.Size.Megabyte);

            InitialLogFileSize = 64 * Constants.Size.Kilobyte;

            MaxScratchBufferSize = ((sizeof(int) == IntPtr.Size ? 32 : 256) * Constants.Size.Megabyte);

            MaxNumberOfPagesInJournalBeforeFlush =
                ((sizeof(int) == IntPtr.Size ? 4 : 32) * Constants.Size.Megabyte) / Constants.Storage.PageSize;

            IdleFlushTimeout = 5000; // 5 seconds

            OwnsPagers = true;

            IncrementalBackupEnabled = false;

            IoMetrics = new IoMetrics(256, 256, ioChangesNotifications);

            _log = LoggingSource.Instance.GetLogger<StorageEnvironment>(tempPath.FullPath);

            _catastrophicFailureNotification = catastrophicFailureNotification ?? new CatastrophicFailureNotification((e) =>
            {
                if (_log.IsOperationsEnabled)
                    _log.Operations($"Catastrophic failure in {this}", e);
            });

            var shouldForceEnvVar = Environment.GetEnvironmentVariable("VORON_INTERNAL_ForceUsing32BitsPager");

            bool result;
            if (bool.TryParse(shouldForceEnvVar, out result))
                ForceUsing32BitsPager = result;
        }

        public void SetCatastrophicFailure(ExceptionDispatchInfo exception)
        {
            _catastrophicFailure = exception;
            _catastrophicFailureNotification.RaiseNotificationOnce(exception.SourceException);
        }

        internal void AssertNoCatastrophicFailure()
        {
            _catastrophicFailure?.Throw(); // force re-throw of error
        }

        public static StorageEnvironmentOptions CreateMemoryOnly(string name, string tempPath, IoChangesNotifications ioChangesNotifications, CatastrophicFailureNotification catastrophicFailureNotification)
        {
            var tempPathSetting = new VoronPathSetting(tempPath ?? GetTempPath());
            return new PureMemoryStorageEnvironmentOptions(name, tempPathSetting, ioChangesNotifications, catastrophicFailureNotification);
        }

        public static StorageEnvironmentOptions CreateMemoryOnly()
        {
            return CreateMemoryOnly(null, null, null, null);
        }

        public static StorageEnvironmentOptions ForPath(string path, string tempPath, string journalPath, IoChangesNotifications ioChangesNotifications, CatastrophicFailureNotification catastrophicFailureNotification)
        {
            var pathSetting = new VoronPathSetting(path);
            var tempPathSetting = new VoronPathSetting(tempPath ?? GetTempPath(path));
            var journalPathSetting = journalPath != null ? new VoronPathSetting(journalPath) : null;

            return new DirectoryStorageEnvironmentOptions(pathSetting, tempPathSetting, journalPathSetting, ioChangesNotifications, catastrophicFailureNotification);
        }

        public static StorageEnvironmentOptions ForPath(string path)
        {
            return ForPath(path, null, null, null, null);
        }

        private static string GetTempPath(string basePath = null)
        {
            bool useSystemTemp = false;
            // We need to use a Temp directory for storage. There's two ways to do this: either the user provides a full path
            // to use as base (because they expect all temporary files to be stored under it too), or we use the current
            // running directory.
            string tempPath = Path.Combine(basePath ?? Directory.GetCurrentDirectory(), "Temp");
            try
            {
                Directory.CreateDirectory(tempPath);
            }
            catch (UnauthorizedAccessException)
            {
                useSystemTemp = true;
            }

            if (!useSystemTemp)
            {
                // Effective permissions are hard to compute, so we try to create a file and write to it as a check.
                try
                {
                    var tempFilePath = Path.Combine(tempPath, Guid.NewGuid().ToString());
                    File.Create(tempFilePath, 1024).Dispose();
                    File.Delete(tempFilePath);
                }
                catch (Exception)
                {
                    useSystemTemp = true;
                }

            }

            if (useSystemTemp)
                tempPath = Path.GetTempPath();

            return tempPath;
        }

        public class DirectoryStorageEnvironmentOptions : StorageEnvironmentOptions
        {
            private readonly VoronPathSetting _journalPath;
            private readonly VoronPathSetting _basePath;

            private readonly Lazy<AbstractPager> _dataPager;

            private readonly ConcurrentDictionary<string, Lazy<IJournalWriter>> _journals =
                new ConcurrentDictionary<string, Lazy<IJournalWriter>>(StringComparer.OrdinalIgnoreCase);

            public DirectoryStorageEnvironmentOptions(VoronPathSetting basePath, VoronPathSetting tempPath, VoronPathSetting journalPath,
                IoChangesNotifications ioChangesNotifications, CatastrophicFailureNotification catastrophicFailureNotification)
                : base(tempPath ?? basePath, ioChangesNotifications, catastrophicFailureNotification)
            {
                _basePath = basePath;
                _journalPath = journalPath ?? basePath;

                if (Directory.Exists(_basePath.FullPath) == false)
                    Directory.CreateDirectory(_basePath.FullPath);

                if (Equals(_basePath, tempPath) == false && Directory.Exists(TempPath.FullPath) == false)
                    Directory.CreateDirectory(TempPath.FullPath);

                if (Equals(_journalPath, tempPath) == false && Directory.Exists(_journalPath.FullPath) == false)
                    Directory.CreateDirectory(_journalPath.FullPath);

                _dataPager = new Lazy<AbstractPager>(() =>
                {
                    FilePath = _basePath.Combine(Constants.DatabaseFilename);

                    return GetMemoryMapPager(this, InitialFileSize, FilePath, usePageProtection: true);
                });

                // have to be before the journal check, so we'll fail on files in use
                // TODO: Need to verify behavior on Linux, might need to maintain a file lock
                DeleteAllTempBuffers();


                GatherRecyclableJournalFiles(); // if there are any (e.g. after a rude db shut down) let us reuse them

            }

            private void GatherRecyclableJournalFiles()
            {
                foreach (var reusableFile in GetRecyclableJournalFiles())
                {
                    var reuseNameWithoutExt = Path.GetExtension(reusableFile).Substring(1);

                    long reuseNum;
                    if (long.TryParse(reuseNameWithoutExt, out reuseNum))
                    {
                        _reuseCounter = Math.Max(_reuseCounter, reuseNum);
                    }

                    try
                    {
                        var lastWriteTimeUtcTicks = new FileInfo(reusableFile).LastWriteTimeUtc.Ticks;

                        while (_journalsForReuse.ContainsKey(lastWriteTimeUtcTicks))
                        {
                            lastWriteTimeUtcTicks++;
                        }

                        _journalsForReuse[lastWriteTimeUtcTicks] = reusableFile;
                    }
                    catch (Exception ex)
                    {
                        if (_log.IsInfoEnabled)
                            _log.Info("On Storage Environment Options : Can't store journal for reuse : " + reusableFile, ex);
                        TryDelete(reusableFile);
                    }
                }
            }

            private string[] GetRecyclableJournalFiles()
            {
                try
                {
                    return Directory.GetFiles(_journalPath.FullPath, $"{RecyclableJournalFileNamePrefix}.*");
                }
                catch (Exception)
                {
                    return new string[0];
                }
            }

            public VoronPathSetting FilePath { get; private set; }

            public override string ToString()
            {
                return _basePath.FullPath;
            }

            public override AbstractPager DataPager
            {
                get { return _dataPager.Value; }
            }

            public override VoronPathSetting BasePath
            {
                get { return _basePath; }
            }

            public override AbstractPager OpenPager(VoronPathSetting filename)
            {
                return GetMemoryMapPagerInternal(this, null, filename);
            }


            public override IJournalWriter CreateJournalWriter(long journalNumber, long journalSize)
            {
                var name = JournalName(journalNumber);
                var path = _journalPath.Combine(name);
                if (File.Exists(path.FullPath) == false)
                    AttemptToReuseJournal(path, journalSize);

                var result = _journals.GetOrAdd(name, _ => new Lazy<IJournalWriter>(() =>
                {
                    if (RunningOnPosix)
                        return new PosixJournalWriter(this, path, journalSize);

                    return new Win32FileJournalWriter(this, path, journalSize);
                }));

                bool createJournal;
                try
                {
                    createJournal = result.Value.Disposed;
                }
                catch
                {
                    Lazy<IJournalWriter> _;
                    _journals.TryRemove(name, out _);
                    throw;
                }

                if (createJournal)
                {
                    var newWriter = new Lazy<IJournalWriter>(() =>
                    {
                        if (RunningOnPosix)
                            return new PosixJournalWriter(this, path, journalSize);

                        return new Win32FileJournalWriter(this, path, journalSize);
                    });
                    if (_journals.TryUpdate(name, newWriter, result) == false)
                        throw new InvalidOperationException("Could not update journal pager");
                    result = newWriter;
                }

                return result.Value;
            }

            public override VoronPathSetting GetJournalPath(long journalNumber)
            {
                var name = JournalName(journalNumber);
                return _journalPath.Combine(name);
            }

            private static readonly long TickInHour = TimeSpan.FromHours(1).Ticks;

            private void AttemptToReuseJournal(VoronPathSetting desiredPath, long desiredSize)
            {
                lock (_journalsForReuse)
                {
                    var lastModified = DateTime.MinValue.Ticks;
                    while (_journalsForReuse.Count > 0)
                    {
                        lastModified = _journalsForReuse.Keys[_journalsForReuse.Count - 1];
                        var filename = _journalsForReuse.Values[_journalsForReuse.Count - 1];
                        _journalsForReuse.RemoveAt(_journalsForReuse.Count - 1);

                        try
                        {
                            if (File.Exists(filename) == false)
                                continue;

                            File.Move(filename, desiredPath.FullPath);
                            break;
                        }
                        catch (Exception ex)
                        {
                            TryDelete(filename);

                            if (_log.IsInfoEnabled)
                                _log.Info("Failed to rename " + filename + " to " + desiredPath, ex);
                        }
                    }

                    while (_journalsForReuse.Count > 0)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(_journalsForReuse.Values[0]);
                            if (fileInfo.Exists == false)
                            {
                                _journalsForReuse.RemoveAt(0);
                                continue;
                            }

                            if (lastModified - fileInfo.LastWriteTimeUtc.Ticks > TickInHour * 72)
                            {
                                _journalsForReuse.RemoveAt(0);
                                TryDelete(fileInfo.FullName);
                                continue;
                            }

                            if (fileInfo.Length < desiredSize)
                            {
                                _journalsForReuse.RemoveAt(0);
                                TryDelete(fileInfo.FullName);

                                continue;
                            }

                        }
                        catch (IOException)
                        {
                            // explicitly ignoring any such file errors
                            _journalsForReuse.RemoveAt(0);
                            TryDelete(_journalsForReuse.Values[0]);
                        }
                        break;
                    }

                }
            }

            private void TryDelete(string file)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    if (_log.IsInfoEnabled)
                        _log.Info("Failed to delete " + file, ex);
                }
            }

            protected override void Disposing()
            {
                if (Disposed)
                    return;

                Disposed = true;
                if (_dataPager.IsValueCreated)
                    _dataPager.Value.Dispose();
                foreach (var journal in _journals)
                {
                    if (journal.Value.IsValueCreated)
                        journal.Value.Value.Dispose();
                }

                lock (_journalsForReuse)
                {
                    foreach (var reusableFile in _journalsForReuse.Values)
                    {
                        try
                        {
                            File.Delete(reusableFile);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }

            public override bool TryDeleteJournal(long number)
            {
                var name = JournalName(number);

                Lazy<IJournalWriter> lazy;
                if (_journals.TryRemove(name, out lazy) && lazy.IsValueCreated)
                    lazy.Value.Dispose();

                var file = _journalPath.Combine(name);
                if (File.Exists(file.FullPath) == false)
                    return false;

                File.Delete(file.FullPath);

                return true;
            }

            public override unsafe bool ReadHeader(string filename, FileHeader* header)
            {
                var path = _basePath.Combine(filename);
                if (File.Exists(path.FullPath) == false)
                {
                    return false;
                }

                var success = RunningOnPosix ?
                    PosixHelper.TryReadFileHeader(header, path) :
                    Win32Helper.TryReadFileHeader(header, path);

                if (!success)
                    return false;

                return header->Hash == HeaderAccessor.CalculateFileHeaderHash(header);
            }


            public override unsafe void WriteHeader(string filename, FileHeader* header)
            {
                var path = _basePath.Combine(filename);
                if (RunningOnPosix)
                    PosixHelper.WriteFileHeader(header, path);
                else
                    Win32Helper.WriteFileHeader(header, path);
            }

            public void DeleteAllTempBuffers()
            {
                if (Directory.Exists(TempPath.FullPath) == false)
                    return;

                foreach (var file in Directory.GetFiles(TempPath.FullPath, "*.buffers"))
                    File.Delete(file);
            }

            public override AbstractPager CreateScratchPager(string name, long initialSize)
            {
                var scratchFile = TempPath.Combine(name);
                if (File.Exists(scratchFile.FullPath))
                    File.Delete(scratchFile.FullPath);

                return GetMemoryMapPager(this, initialSize, scratchFile, deleteOnClose: true);
            }

            // This is used for special pagers that are used as temp buffers and don't 
            // require encryption: compression, recovery, lazyTxBuffer.
            public override AbstractPager CreateTemporaryBufferPager(string name, long initialSize)
            {
                var scratchFile = TempPath.Combine(name);
                if (File.Exists(scratchFile.FullPath))
                    File.Delete(scratchFile.FullPath);

                return GetMemoryMapPagerInternal(this, initialSize, scratchFile, deleteOnClose: true);
            }

            private AbstractPager GetMemoryMapPager(StorageEnvironmentOptions options, long? initialSize, VoronPathSetting file,
                bool deleteOnClose = false,
                bool usePageProtection = false)
            {
                var pager = GetMemoryMapPagerInternal(options, initialSize, file, deleteOnClose, usePageProtection);

                return EncryptionEnabled == false
                    ? pager
                    : new CryptoPager(pager);
            }

            private AbstractPager GetMemoryMapPagerInternal(StorageEnvironmentOptions options, long? initialSize, VoronPathSetting file, bool deleteOnClose = false, bool usePageProtection = false)
            {
                if (RunningOnPosix)
                {
                    if (RunningOn32Bits)
                    {
                        return new Posix32BitsMemoryMapPager(options, file, initialSize,
                            usePageProtection: usePageProtection)
                        {
                            DeleteOnClose = deleteOnClose
                        };
                    }
                    return new PosixMemoryMapPager(options, file, initialSize, usePageProtection: usePageProtection)
                    {
                        DeleteOnClose = deleteOnClose
                    };
                }

                var attributes = deleteOnClose
                    ? Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary | Win32NativeFileAttributes.RandomAccess
                    : Win32NativeFileAttributes.Normal;

                if (RunningOn32Bits)
                    return new Windows32BitsMemoryMapPager(options, file, initialSize, attributes, usePageProtection: usePageProtection);

                return new WindowsMemoryMapPager(options, file, initialSize, attributes, usePageProtection: usePageProtection);
            }

            public override AbstractPager OpenJournalPager(long journalNumber)
            {
                var name = JournalName(journalNumber);
                var path = _journalPath.Combine(name);
                var fileInfo = new FileInfo(path.FullPath);
                if (fileInfo.Exists == false)
                    throw new InvalidOperationException("No such journal " + path);

                if (fileInfo.Length < InitialLogFileSize)
                {
                    EnsureMinimumSize(fileInfo, path);
                }

                if (RunningOnPosix)
                {
                    if (RunningOn32Bits)
                        return new Posix32BitsMemoryMapPager(this, path);
                    return new PosixMemoryMapPager(this, path);
                }

                if (RunningOn32Bits)
                    return new Windows32BitsMemoryMapPager(this, path, access: Win32NativeFileAccess.GenericRead,
                        fileAttributes: Win32NativeFileAttributes.SequentialScan);

                var windowsMemoryMapPager = new WindowsMemoryMapPager(this, path, access: Win32NativeFileAccess.GenericRead,
                    fileAttributes: Win32NativeFileAttributes.SequentialScan);
                windowsMemoryMapPager.TryPrefetchingWholeFile();
                return windowsMemoryMapPager;
            }

            private void EnsureMinimumSize(FileInfo fileInfo, VoronPathSetting path)
            {
                try
                {
                    using (var stream = fileInfo.Open(FileMode.OpenOrCreate))
                    {
                        stream.SetLength(InitialLogFileSize);
                    }
                }
                catch (Exception e)
                {
                    throw new InvalidOperationException(
                        "Journal file " + path + " could not be opened because it's size is too small and we couldn't increase it",
                        e);
                }
            }
        }

        public class PureMemoryStorageEnvironmentOptions : StorageEnvironmentOptions
        {
            private readonly string _name;
            private static int _counter;

            private readonly Lazy<AbstractPager> _dataPager;

            private readonly Dictionary<string, IJournalWriter> _logs =
                new Dictionary<string, IJournalWriter>(StringComparer.OrdinalIgnoreCase);

            private readonly Dictionary<string, IntPtr> _headers =
                new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
            private readonly int _instanceId;

            public override void SetPosixOptions()
            {
                PosixOpenFlags = DefaultPosixFlags;
            }

            public PureMemoryStorageEnvironmentOptions(string name, VoronPathSetting tempPath,
                IoChangesNotifications ioChangesNotifications, CatastrophicFailureNotification catastrophicFailureNotification)
                : base(tempPath, ioChangesNotifications, catastrophicFailureNotification)
            {
                _name = name;
                _instanceId = Interlocked.Increment(ref _counter);
                var guid = Guid.NewGuid();
                var filename = $"ravendb-{Process.GetCurrentProcess().Id}-{_instanceId}-data.pager-{guid}";

                WinOpenFlags = Win32NativeFileAttributes.Temporary | Win32NativeFileAttributes.DeleteOnClose;

                if (Directory.Exists(tempPath.FullPath) == false)
                    Directory.CreateDirectory(tempPath.FullPath);

                _dataPager = new Lazy<AbstractPager>(() => GetTempMemoryMapPager(this, TempPath.Combine(filename), InitialFileSize,
                    Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary), true);
            }

            public override string ToString()
            {
                return "mem #" + _instanceId + " " + _name;
            }

            public override AbstractPager DataPager => _dataPager.Value;

            public override VoronPathSetting BasePath { get; } = new MemoryVoronPathSetting();

            public override IJournalWriter CreateJournalWriter(long journalNumber, long journalSize)
            {
                var name = JournalName(journalNumber);
                IJournalWriter value;
                if (_logs.TryGetValue(name, out value))
                    return value;

                var path = GetJournalPath(journalNumber);

                if (RunningOnPosix)
                {
                    value = new PosixJournalWriter(this, path, journalSize);
                }
                else
                {
                    value = new Win32FileJournalWriter(this, path, journalSize,
                        Win32NativeFileAccess.GenericWrite,
                        Win32NativeFileShare.Read | Win32NativeFileShare.Write | Win32NativeFileShare.Delete
                        );
                }

                _logs[name] = value;
                return value;
            }

            public override VoronPathSetting GetJournalPath(long journalNumber)
            {
                var name = JournalName(journalNumber);
                var filename = $"ravendb-{Process.GetCurrentProcess().Id}-{_instanceId}-{name}-{Guid.NewGuid()}";

                return TempPath.Combine(filename);
            }

            protected override void Disposing()
            {
                if (Disposed)
                    return;
                Disposed = true;

                _dataPager.Value.Dispose();
                foreach (var virtualPager in _logs)
                {
                    virtualPager.Value.Dispose();
                }

                foreach (var headerSpace in _headers)
                {
                    Marshal.FreeHGlobal(headerSpace.Value);
                }

                _headers.Clear();
            }

            public override bool TryDeleteJournal(long number)
            {
                var name = JournalName(number);
                IJournalWriter value;
                if (_logs.TryGetValue(name, out value) == false)
                    return false;
                _logs.Remove(name);
                value.Dispose();
                return true;
            }

            public override unsafe bool ReadHeader(string filename, FileHeader* header)
            {
                if (Disposed)
                    throw new ObjectDisposedException("PureMemoryStorageEnvironmentOptions");
                IntPtr ptr;
                if (_headers.TryGetValue(filename, out ptr) == false)
                {
                    return false;
                }
                *header = *((FileHeader*)ptr);

                return header->Hash == HeaderAccessor.CalculateFileHeaderHash(header);
            }

            public override unsafe void WriteHeader(string filename, FileHeader* header)
            {
                if (Disposed)
                    throw new ObjectDisposedException("PureMemoryStorageEnvironmentOptions");

                IntPtr ptr;
                if (_headers.TryGetValue(filename, out ptr) == false)
                {
                    ptr = (IntPtr)NativeMemory.AllocateMemory(sizeof(FileHeader));
                    _headers[filename] = ptr;
                }
                Memory.Copy((byte*)ptr, (byte*)header, sizeof(FileHeader));
            }

            private AbstractPager GetTempMemoryMapPager(PureMemoryStorageEnvironmentOptions options, VoronPathSetting path, long? intialSize, Win32NativeFileAttributes win32NativeFileAttributes)
            {
                var pager = GetTempMemoryMapPagerInternal(options, path, intialSize,
                    Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary);
                return EncryptionEnabled == false
                    ? pager
                    : new CryptoPager(pager);
            }

            private AbstractPager GetTempMemoryMapPagerInternal(PureMemoryStorageEnvironmentOptions options, VoronPathSetting path, long? intialSize, Win32NativeFileAttributes win32NativeFileAttributes)
            {
                if (RunningOnPosix)
                {
                    if (RunningOn32Bits)
                        return new PosixTempMemoryMapPager(options, path, intialSize); // need to change to 32 bit pager

                    return new PosixTempMemoryMapPager(options, path, intialSize);
                }
                if (RunningOn32Bits)
                    return new Windows32BitsMemoryMapPager(options, path, intialSize,
                        Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary);

                return new WindowsMemoryMapPager(options, path, intialSize,
                        Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary);
            }

            public override AbstractPager CreateScratchPager(string name, long initialSize)
            {
                var guid = Guid.NewGuid();
                var filename = $"ravendb-{Process.GetCurrentProcess().Id}-{_instanceId}-{name}-{guid}";

                return GetTempMemoryMapPager(this, TempPath.Combine(filename), initialSize, Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary);
            }

            public override AbstractPager CreateTemporaryBufferPager(string name, long initialSize)
            {
                var guid = Guid.NewGuid();
                var filename = $"ravendb-{Process.GetCurrentProcess().Id}-{_instanceId}-{name}-{guid}";

                return GetTempMemoryMapPagerInternal(this, TempPath.Combine(filename), initialSize, Win32NativeFileAttributes.RandomAccess | Win32NativeFileAttributes.DeleteOnClose | Win32NativeFileAttributes.Temporary);
            }

            public override AbstractPager OpenPager(VoronPathSetting filename)
            {
                var pager = OpenPagerInternal(filename);

                return EncryptionEnabled == false ? pager : new CryptoPager(pager);
            }

            private AbstractPager OpenPagerInternal(VoronPathSetting filename)
            {
                if (RunningOnPosix)
                {
                    if (RunningOn32Bits)
                        return new Posix32BitsMemoryMapPager(this, filename);
                    return new PosixMemoryMapPager(this, filename);
                }

                if (RunningOn32Bits)
                    return new Windows32BitsMemoryMapPager(this, filename);

                return new WindowsMemoryMapPager(this, filename);
            }

            public override AbstractPager OpenJournalPager(long journalNumber)
            {
                var name = JournalName(journalNumber);
                IJournalWriter value;
                if (_logs.TryGetValue(name, out value))
                    return value.CreatePager();
                throw new InvalidOperationException("No such journal " + journalNumber);
            }
        }

        public static string JournalName(long number)
        {
            return string.Format("{0:D19}.journal", number);
        }

        public static string RecyclableJournalName(long number)
        {
            return $"{RecyclableJournalFileNamePrefix}.{number:D19}";
        }

        public static string JournalRecoveryName(long number)
        {
            return string.Format("{0:D19}.recovery", number);
        }

        public static string ScratchBufferName(long number)
        {
            return string.Format("scratch.{0:D10}.buffers", number);
        }

        public unsafe void Dispose()
        {
            var copy = MasterKey;
            if (copy != null)
            {
                fixed (byte* key = copy)
                {
                    Sodium.ZeroMemory(key, copy.Length);
                    MasterKey = null;
                }
            }
            Disposing();
        }

        protected abstract void Disposing();

        public abstract bool TryDeleteJournal(long number);

        public abstract unsafe bool ReadHeader(string filename, FileHeader* header);

        public abstract unsafe void WriteHeader(string filename, FileHeader* header);

        public abstract AbstractPager CreateScratchPager(string name, long initialSize);

        // Used for special temporary pagers (compression, recovery, lazyTX...) which should not be wrapped by the crypto pager.
        public abstract AbstractPager CreateTemporaryBufferPager(string name, long initialSize);

        public abstract AbstractPager OpenJournalPager(long journalNumber);

        public abstract AbstractPager OpenPager(VoronPathSetting filename);

        public bool EncryptionEnabled => MasterKey != null;

        public static bool RunningOnPosix
            => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
               RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        public bool RunningOn32Bits => IntPtr.Size == sizeof(int) || ForceUsing32BitsPager;


        public TransactionsMode TransactionsMode { get; set; }
        public OpenFlags PosixOpenFlags;
        public Win32NativeFileAttributes WinOpenFlags = SafeWin32OpenFlags;
        public DateTime? NonSafeTransactionExpiration { get; set; }
        public TimeSpan DisposeWaitTime { get; set; }

        public int NumOfConcurrentSyncsPerPhysDrive
        {
            get
            {
                if (_numOfConcurrentSyncsPerPhysDrive < 1)
                    _numOfConcurrentSyncsPerPhysDrive = 3;
                return _numOfConcurrentSyncsPerPhysDrive;
            }
            set => _numOfConcurrentSyncsPerPhysDrive = value;
        }

        public int TimeToSyncAfterFlashInSec
        {
            get
            {
                if (_timeToSyncAfterFlashInSec < 1)
                    _timeToSyncAfterFlashInSec = 30;
                return _timeToSyncAfterFlashInSec;
            }
            set => _timeToSyncAfterFlashInSec = value;
        }

        public byte[] MasterKey;

        public const Win32NativeFileAttributes SafeWin32OpenFlags = Win32NativeFileAttributes.Write_Through | Win32NativeFileAttributes.NoBuffering;
        public OpenFlags DefaultPosixFlags = PlatformDetails.Is32Bits ? PerPlatformValues.OpenFlags.O_LARGEFILE : 0;
        public OpenFlags SafePosixOpenFlags = PerPlatformValues.OpenFlags.O_DSYNC | PerPlatformValues.OpenFlags.O_DIRECT;
        private readonly Logger _log;

        private readonly SortedList<long, string> _journalsForReuse =
            new SortedList<long, string>();

        private int _numOfConcurrentSyncsPerPhysDrive;
        private int _timeToSyncAfterFlashInSec;
        public long CompressTxAboveSizeInBytes;

        public virtual void SetPosixOptions()
        {
            if (PlatformDetails.RunningOnPosix == false)
                return;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return; // osx supports F_NOCACHE

            if (BasePath != null && StorageEnvironment.IsStorageSupportingO_Direct(_log, BasePath.FullPath) == false)
            {
                SafePosixOpenFlags &= ~PerPlatformValues.OpenFlags.O_DIRECT;
                var message = "Path " + BasePath +
                              " not supporting O_DIRECT writes. As a result - data durability is not guaranteed";
                var details = $"Storage type '{PosixHelper.GetFileSystemOfPath(BasePath.FullPath)}' doesn't support direct write to disk (non durable file system)";
                InvokeNonDurableFileSystemError(this, message, new NonDurableFileSystemException(message), details);
            }

            PosixOpenFlags = SafePosixOpenFlags;
        }

        public void TryStoreJournalForReuse(VoronPathSetting filename)
        {
            try
            {
                var fileModifiedDate = new FileInfo(filename.FullPath).LastWriteTimeUtc;
                var counter = Interlocked.Increment(ref _reuseCounter);
                var newName = Path.Combine(Path.GetDirectoryName(filename.FullPath), RecyclableJournalName(counter));

                File.Move(filename.FullPath, newName);
                lock (_journalsForReuse)
                {
                    var ticks = fileModifiedDate.Ticks;

                    while (_journalsForReuse.ContainsKey(ticks))
                        ticks++;

                    _journalsForReuse[ticks] = newName;
                }
            }
            catch (Exception ex)
            {
                if (_log.IsInfoEnabled)
                    _log.Info("Can't store journal for reuse : " + filename, ex);
                try
                {
                    if (File.Exists(filename.FullPath))
                        File.Delete(filename.FullPath);
                }
                catch
                {
                    // nothing we can do about it
                }
            }
        }
    }
}
