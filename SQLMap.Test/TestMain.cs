using System;
using System.IO;
using FluentAssertions;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;

namespace SQLMap.Test
{
    public class TestMain
    {
        private readonly Logger _logger = LogManager.GetLogger("Test");

        [OneTimeSetUp]
        public void SetupNLog()
        {
            LogManager.Configuration = null;

            var config = new LoggingConfiguration();
            var loglevel = LogLevel.Debug;

            var layout = @"${message}";

            var consoleTarget = new ColoredConsoleTarget();

            config.AddTarget("console", consoleTarget);

            consoleTarget.Layout = layout;

            var rule1 = new LoggingRule("*", loglevel, consoleTarget);
            config.LoggingRules.Add(rule1);

            LogManager.Configuration = config;
        }

        [Test]
        public void FindSqlFiles()
        {
            _logger.Info("Looking for SQLite databases...");

            var dir = @"..\..\..\TestFiles";

            var foundFiles = 0;

            foreach (var fileSystemEntry in Directory.GetFiles(dir,"*",SearchOption.AllDirectories))
            {
                _logger.Trace($"Checking '{fileSystemEntry}'");

                try
                {
                    if (SQLiteFile.IsSQLiteFile(fileSystemEntry))
                    {
                        _logger.Info($"\t '{fileSystemEntry}' is a sqlite database");
                        foundFiles += 1;
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e);
                    
                }
            }

            foundFiles.Should().Be(1);

        }

        [Test]
        public void Test()
        {
            var s = new SQLMap();
            s.Test(@"..\..\..\TestFiles\Contacts.db");
        }
    }
}
