using log4net;
using System;
using System.Collections.Specialized;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using Topshelf;

namespace DVSA_Service
{
    class Program
    {
        public enum ExitCode { Normal = 0, NoDbConnection, SqlError };
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly SqlConnection connection = new SqlConnection(ConfigurationManager.ConnectionStrings["Test"].ConnectionString);
        private static readonly NameValueCollection appSettings = ConfigurationManager.AppSettings;
        private static readonly string path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().CodeBase).Replace(@"file:\", "");

        static void Main(string[] args)
        {
            log4net.Config.XmlConfigurator.Configure(new FileInfo(Path.Combine(path, "log4net.config")));
            TopshelfExitCode returnCode = HostFactory.Run(app =>
            {
                app.UseLog4Net();
                app.Service<Worker>(service =>
                {
                    string dvsaConfig = File.ReadAllText(Path.Combine(path, "config.json"));
                    service.ConstructUsing(name => new Worker(connection, appSettings, dvsaConfig));
                    service.WhenStarted(worker => worker.Start());
                    service.WhenStopped(worker => worker.Stop());
                });
                app.StartAutomatically();
                app.RunAsLocalService();
                app.SetServiceName("DVSA Service");
                app.SetDescription("Checks vehicles against the DVSA MOT History API");

                app.EnableServiceRecovery(recovery =>
                {
                    recovery.RestartService(5);
                });
            });

            int exitCode = (int)Convert.ChangeType(returnCode, returnCode.GetTypeCode());
            Environment.ExitCode = exitCode;
        }
    }
}
