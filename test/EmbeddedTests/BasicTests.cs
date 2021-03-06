﻿using System.IO;
using Raven.Client.Documents.Conventions;
using Raven.Embedded;
using Xunit;

namespace EmbeddedTests
{
    public class BasicTests : EmbeddedTestBase
    {
        [Fact]
        public void TestEmbedded()
        {
            var paths = CopyServer();

            using (var embedded = new EmbeddedServer())
            {
                embedded.StartServer(new ServerOptions
                {
                    ServerDirectory = paths.ServerDirectory,
                    DataDirectory = paths.DataDirectory,
                });

                using (var store = embedded.GetDocumentStore(new DatabaseOptions("Test")
                {
                    Conventions = new DocumentConventions
                    {
                        SaveEnumsAsIntegers = true
                    }
                }))
                {
                    Assert.True(store.Conventions.SaveEnumsAsIntegers);
                    Assert.True(store.GetRequestExecutor().Conventions.SaveEnumsAsIntegers);

                    using (var session = store.OpenSession())
                    {
                        session.Store(new Person
                        {
                            Name = "John"
                        }, "people/1");

                        session.SaveChanges();
                    }
                }
            }

            using (var embedded = new EmbeddedServer())
            {
                embedded.StartServer(new ServerOptions
                {
                    ServerDirectory = paths.ServerDirectory,
                    DataDirectory = paths.DataDirectory,
                });

                using (var store = embedded.GetDocumentStore("Test"))
                {
                    Assert.False(store.Conventions.SaveEnumsAsIntegers);
                    Assert.False(store.GetRequestExecutor().Conventions.SaveEnumsAsIntegers);

                    using (var session = store.OpenSession())
                    {
                        var person = session.Load<Person>("people/1");

                        Assert.NotNull(person);
                        Assert.Equal("John", person.Name);
                    }
                }
            }
        }

        private (string ServerDirectory, string DataDirectory) CopyServer()
        {
            var baseDirectory = NewDataPath();
            var serverDirectory = Path.Combine(baseDirectory, "RavenDBServer");
            var dataDirectory = Path.Combine(baseDirectory, "RavenDB");

            if (Directory.Exists(serverDirectory) == false)
                Directory.CreateDirectory(serverDirectory);

            if (Directory.Exists(dataDirectory) == false)
                Directory.CreateDirectory(dataDirectory);

#if DEBUG
            var runtimeConfigPath = @"../../../../../src/Raven.Server/bin/x64/Debug/netcoreapp3.1/Raven.Server.runtimeconfig.json";
            if (File.Exists(runtimeConfigPath) == false) // this can happen when running directly from CLI e.g. dotnet xunit
                runtimeConfigPath = @"../../../../../src/Raven.Server/bin/Debug/netcoreapp3.1/Raven.Server.runtimeconfig.json";
#else
                var runtimeConfigPath = @"../../../../../src/Raven.Server/bin/x64/Release/netcoreapp3.1/Raven.Server.runtimeconfig.json";
                if (File.Exists(runtimeConfigPath) == false) // this can happen when running directly from CLI e.g. dotnet xunit
                    runtimeConfigPath = @"../../../../../src/Raven.Server/bin/Release/netcoreapp3.1/Raven.Server.runtimeconfig.json";
#endif

            var runtimeConfigFileInfo = new FileInfo(runtimeConfigPath);
            if (runtimeConfigFileInfo.Exists == false)
                throw new FileNotFoundException("Could not find runtime config", runtimeConfigPath);

            File.Copy(runtimeConfigPath, Path.Combine(serverDirectory, runtimeConfigFileInfo.Name));

            foreach (var extension in new[] { "*.dll", "*.so", "*.dylib", "*.deps.json" })
            {
                foreach (var file in Directory.GetFiles(runtimeConfigFileInfo.DirectoryName, extension))
                {
                    var fileInfo = new FileInfo(file);
                    File.Copy(file, Path.Combine(serverDirectory, fileInfo.Name), true);
                }
            }

            var runtimesSource = Path.Combine(runtimeConfigFileInfo.DirectoryName, "runtimes");
            var runtimesDestination = Path.Combine(serverDirectory, "runtimes");

            foreach (string dirPath in Directory.GetDirectories(runtimesSource, "*",
                SearchOption.AllDirectories))
                Directory.CreateDirectory(dirPath.Replace(runtimesSource, runtimesDestination));

            foreach (string newPath in Directory.GetFiles(runtimesSource, "*.*",
                SearchOption.AllDirectories))
                File.Copy(newPath, newPath.Replace(runtimesSource, runtimesDestination), true);

            return (serverDirectory, dataDirectory);
        }



        private class Person
        {
            public string Id { get; set; }

            public string Name { get; set; }
        }
    }
}
