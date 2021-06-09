using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using log4net.Repository.Hierarchy;

namespace extractor
{
    public static class Log
    {
        private static readonly ILog log = LogManager.GetLogger("Extractor");

        // https://stackoverflow.com/questions/16336917/can-you-configure-log4net-in-code-instead-of-using-a-config-file
        public static void SetOutput(string path, bool verbose)
        {
            var hierarchy = (Hierarchy) LogManager.GetRepository();

            var appender = new RollingFileAppender();
            appender.Name = "FileAppender";
            appender.File = path;
            appender.AppendToFile = false;
            appender.StaticLogFileName = true;

            var layout = new PatternLayout();
            layout.ConversionPattern = "%d %c [%p]: %m%n";
            layout.ActivateOptions();

            appender.Layout = layout;
            appender.ActivateOptions();
            hierarchy.Root.AddAppender(appender);

            hierarchy.Root.Level = verbose ? Level.All : Level.Warn;
            hierarchy.Configured = true;
        }

        public static void Info(string message)
        {
            log.Info(message);
        }

        public static void Debug(string message)
        {
            log.Debug(message);
        }

        public static void Warn(string message)
        {
            log.Warn(message);
        }

        public static void Error(string message)
        {
            log.Error(message);
        }
    }
}