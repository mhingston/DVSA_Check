using log4net;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Specialized;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace DVSA_Service
{
    class Worker
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(Worker));
        private SqlConnection connection;
        private readonly NameValueCollection appSettings;
        private readonly DVSA dvsa;
        private DataTable pending = new DataTable();
 
        public Worker(SqlConnection connection, NameValueCollection appSettings, string dvsaConfig)
        {
            this.connection = connection;
            this.appSettings = appSettings;
            dvsa = new DVSA(dvsaConfig);
        }

        private async Task ReconnectAsync()
        {
            if (connection.State != ConnectionState.Open)
            {
                try
                {
                    connection.Close();
                    await connection.OpenAsync();
                }

                catch (Exception error)
                {
                    log.Fatal("Unable to connect to database.", error);
                    connection.Dispose();
                    dvsa.Dispose();
                    Environment.Exit((int)Program.ExitCode.NoDbConnection);
                }
            }
        }

        private async Task GetBatchAsync()
        {
            await ProcessPendingRequestsAsync();
            log.Info("Fetching batch...");
            await ReconnectAsync();

            using (SqlCommand cmd = new SqlCommand())
            {
                cmd.CommandText = "DVSA_qListPending";
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Connection = connection;
                DataTable dataTable = new DataTable();

                try
                {
                    SqlDataReader reader = await cmd.ExecuteReaderAsync();
                    dataTable.Load(reader);
                }

                catch (Exception error)
                {
                    log.Fatal("Unable to get batch from database.", error);
                    connection.Dispose();
                    dvsa.Dispose();
                    Environment.Exit((int)Program.ExitCode.SqlError);
                }

                pending = dataTable;

                if (dataTable.Rows.Count == 0)
                {
                    log.Info("No pending rows, sleeping...");
                    await Task.Delay(Convert.ToInt32(appSettings["SleepDurationMs"]));
                }
            }
        }

        private async Task ProcessPendingRequestsAsync()
        {
            await ReconnectAsync();

            using (SqlCommand cmd = new SqlCommand())
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = "DVSA_Request_xProcess";
                cmd.Connection = connection;

                try
                {
                    await cmd.ExecuteNonQueryAsync();

                    #if DEBUG
                        log.Debug("Processed pending requests.");
                    #endif
                }

                catch (Exception error)
                {
                    log.Error("Unable to process pending requests.", error);
                }
            }
        }

        private async Task InsertAsync(DataRow row, JObject json)
        {
            int requestId = 0;
            await ReconnectAsync();

            using (SqlCommand cmd = new SqlCommand())
            {
                DateTime sqlDateTimeMin = new DateTime(1753, 1, 1);
                DateTime sqlDateTimeMax = new DateTime(9999, 12, 31);

                cmd.CommandType = CommandType.StoredProcedure;
                cmd.CommandText = "DVSA_Request_xInsert";
                cmd.Connection = connection;
                cmd.Parameters.AddWithValue("@RowID", row["RowID"]);
                cmd.Parameters.AddWithValue("@DVSA_Request_TypeID", row["DVSA_Request_TypeID"]);
                cmd.Parameters.AddWithValue("@MotDueDate", DateTime.TryParseExact(json["MOTDueDate"]?.ToString(), "dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime motDueDate) == true && motDueDate >= sqlDateTimeMin && motDueDate <= sqlDateTimeMax ? motDueDate : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@RegDate", DateTime.TryParseExact(json["RegDate"]?.ToString(), "dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime regDate) == true && regDate >= sqlDateTimeMin && regDate <= sqlDateTimeMax ? regDate : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@VehicleDVLAStatusID", Convert.ToInt32(json["StatusID"]));
                cmd.Parameters.AddWithValue("@Timestamp", DateTime.Now);
                cmd.Parameters.AddWithValue("@ProcessComplete", false);

                try
                {
                    requestId = (int)(await cmd.ExecuteScalarAsync());
                    
                    #if DEBUG
                        log.Debug($"Inserted DVSA_Request (DVSA_Request: {requestId}).");
                    #endif
                }

                catch (Exception error)
                {
                    log.Error("Unable to insert row into DVSA_Request.", error);
                }
            }
        }

        public void Start()
        {
            log.Info("Service started.");
            StartAsync();
        }

        public async Task StartAsync()
        {
            await GetBatchAsync();

            foreach (DataRow row in pending.Rows)
            {
                string result = await dvsa.LookupAsync(row["RegistrationNumber"].ToString(), Convert.ToBoolean(row["IsNorthernIrelandSite"]));
                JObject json = JObject.Parse(result);
                await InsertAsync(row, json);
                Thread.Sleep(Convert.ToInt32(appSettings["DelayBetweenRequestsMs"]));
            }

            pending.Clear();
            await StartAsync();
        }

        public void Stop()
        {
            log.Info("Service stopped.");
            dvsa.Dispose();
            Environment.Exit((int)Program.ExitCode.Normal);
        }
    }
}
