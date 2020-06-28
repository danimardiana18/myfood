using myfoodapp.Core.Common;
using myfoodapp.Core.Model;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;

namespace myfoodapp.Core.Business
{
    public sealed class MeasureBackgroundTask
    {
        private BackgroundWorker bw = new BackgroundWorker();
        private AtlasSensorManager sensorManager;
        private SigfoxInterfaceManager sigfoxManager;
        private UserSettings userSettings;

        private UserSettingsModel userSettingsModel = UserSettingsModel.GetInstance;
        private LogManager lg = LogManager.GetInstance;
        private DatabaseModel databaseModel = DatabaseModel.GetInstance;

        private int TICKSPERCYCLE = 600000;
        private int TICKSPERCYCLE_DIAGNOSTIC_MODE = 60000;

        public event EventHandler Completed;

        private static MeasureBackgroundTask instance;

        public static MeasureBackgroundTask GetInstance
        {
            get
            {
                if (instance == null)
                {
                    instance = new MeasureBackgroundTask();
                }
                return instance;
            }
        }

        private MeasureBackgroundTask()
        {
            lg.AppendLog(Log.CreateLog("Measure Service starting...", LogType.System));

            userSettings = new UserSettings();

            userSettings = userSettingsModel.GetUserSettings();

            lg.AppendLog(Log.CreateLog("UserSettings retreived", LogType.System));

            //Disable Diagnostic Mode on Restart
            if(userSettings.isDiagnosticModeEnable)
            {
                userSettings.isDiagnosticModeEnable = false;

                userSettingsModel.SyncUserSettings(userSettings);
            }

            bw.WorkerSupportsCancellation = true;
            bw.WorkerReportsProgress = true;
            bw.DoWork += Bw_DoWork;
            bw.RunWorkerCompleted += Bw_RunWorkerCompleted;
            bw.ProgressChanged += Bw_ProgressChanged;
        }

        private void Bw_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            //Messenger.Default.Send(new RefreshDashboardMessage());
        }

        public void Run()
        {
            if (bw.IsBusy)
                return;

            lg.AppendLog(Log.CreateLog("Measure Service running...", LogType.System));
            bw.RunWorkerAsync();
        }

        public void Stop()
        {
            bw.CancelAsync();
        }

        private void Bw_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            lg.AppendLog(Log.CreateLog("Measure Service stopping...", LogType.System));
            Completed?.Invoke(this, EventArgs.Empty);
        }

        private void Bw_DoWork(object sender, DoWorkEventArgs e)
        {
            var watch = Stopwatch.StartNew();

            if (userSettings.measureFrequency >= 60000)
                TICKSPERCYCLE = userSettings.measureFrequency;

            var clockManager = ClockManager.GetInstance;

            var captureDateTime = DateTime.Now;

            if (clockManager != null)
            {
                Task.Run(async() => 
                {
                    clockManager.InitClock();
                    await Task.Delay(2000);
                    captureDateTime = clockManager.ReadDate();
                    clockManager.Dispose();
                }).Wait();               
            }

            userSettings = userSettingsModel.GetUserSettings();

            sigfoxManager = SigfoxInterfaceManager.GetInstance;

            if(userSettings.isSigFoxComEnable)
            {
                sigfoxManager.InitInterface();
            }

            if (userSettings.isDiagnosticModeEnable)
            {
                TICKSPERCYCLE = TICKSPERCYCLE_DIAGNOSTIC_MODE;

                if(userSettings.isSigFoxComEnable && sigfoxManager.isInitialized)
                    sigfoxManager.SendMessage("AAAAAAAAAAAAAAAAAAAAAAAA");
            }
                
            sensorManager = AtlasSensorManager.GetInstance;

            sensorManager.InitSensors(userSettings.isSleepModeEnable);

            sensorManager.SetDebugLedMode(userSettings.isDebugLedEnable);

            var humTempManager = HumidityTemperatureManager.GetInstance;

            if (userSettings.isTempHumiditySensorEnable)
            {
                humTempManager.Connect();
            }

            while (!bw.CancellationPending)
            {
                var elapsedMs = watch.ElapsedMilliseconds;

                try
                {
                    if (elapsedMs % TICKSPERCYCLE == 0)
                    {
                            captureDateTime = captureDateTime.AddMilliseconds(TICKSPERCYCLE);

                            TimeSpan t = TimeSpan.FromMilliseconds(elapsedMs);

                            string logDescription = string.Format("[ {0:d} - {0:t} ] Service running since {1:D2}d:{2:D2}h:{3:D2}m:{4:D2}s",
                                                    captureDateTime,
                                                    t.Days,
                                                    t.Hours,
                                                    t.Minutes,
                                                    t.Seconds,
                                                    t.Milliseconds);

                            lg.AppendLog(Log.CreateLog(logDescription, LogType.Information));

                            var watchMesures = Stopwatch.StartNew();     

                            if (sensorManager.isSensorOnline(SensorTypeEnum.waterTemperature))
                            {
                                if (userSettings.isDiagnosticModeEnable)
                                    lg.AppendLog(Log.CreateLog("Water Temperature capturing", LogType.Information));

                                decimal capturedValue = 0;
                                capturedValue = sensorManager.RecordSensorsMeasure(SensorTypeEnum.waterTemperature, userSettings.isSleepModeEnable);

                                if (capturedValue > -20 && capturedValue < 80)
                                {
                                    if (!userSettings.isDiagnosticModeEnable)
                                    sensorManager.SetWaterTemperatureForPHSensor(capturedValue);

                                        var task = Task.Run(async () =>
                                        {
                                            await databaseModel.AddMesure(captureDateTime, capturedValue, SensorTypeEnum.waterTemperature);
                                        });
                                        task.Wait();
                                
                                        if (userSettings.isDiagnosticModeEnable)
                                        {
                                            lg.AppendLog(Log.CreateLog(String.Format("Water Temperature captured : {0}", capturedValue), LogType.Information));
                                            var status = sensorManager.GetSensorStatus(SensorTypeEnum.waterTemperature, userSettings.isSleepModeEnable);
                                            lg.AppendLog(Log.CreateLog(String.Format("Water Temperature status : {0}", status), LogType.System));
                                        }     
                                }
                                else
                                lg.AppendLog(Log.CreateLog(String.Format("Water Temperature value out of range - {0}", capturedValue), LogType.Warning));
                           }

                            if (sensorManager.isSensorOnline(SensorTypeEnum.pH))
                            {
                                if (userSettings.isDiagnosticModeEnable)
                                    lg.AppendLog(Log.CreateLog("PH capturing", LogType.Information));

                                decimal capturedValue = 0;
                                capturedValue = sensorManager.RecordpHMeasure(userSettings.isSleepModeEnable);

                                if (capturedValue > 1 && capturedValue < 12)
                                {
                                    var task = Task.Run(async () =>
                                    {
                                        await databaseModel.AddMesure(captureDateTime, capturedValue, SensorTypeEnum.pH);
                                    });
                                    task.Wait();

                                    if (userSettings.isDiagnosticModeEnable)
                                    {
                                        lg.AppendLog(Log.CreateLog(String.Format("PH captured : {0}", capturedValue), LogType.Information));
                                        var status = sensorManager.GetSensorStatus(SensorTypeEnum.pH, userSettings.isSleepModeEnable);
                                        lg.AppendLog(Log.CreateLog(String.Format("PH status : {0}", status), LogType.System));
                                    }              
                                }
                                else
                                lg.AppendLog(Log.CreateLog(String.Format("PH value out of range - {0}", capturedValue), LogType.Warning));
                            }

                            if (sensorManager.isSensorOnline(SensorTypeEnum.ORP))
                            {
                                if (userSettings.isDiagnosticModeEnable)
                                   lg.AppendLog(Log.CreateLog("ORP capturing", LogType.Information));

                                decimal capturedValue = 0;
                                capturedValue = sensorManager.RecordSensorsMeasure(SensorTypeEnum.ORP, userSettings.isSleepModeEnable);

                                if (capturedValue > 0 && capturedValue < 1500)
                                {
                                    var task = Task.Run(async () =>
                                    {
                                        await databaseModel.AddMesure(captureDateTime, capturedValue, SensorTypeEnum.ORP);
                                    });
                                    task.Wait();

                                    if (userSettings.isDiagnosticModeEnable)
                                    {
                                        lg.AppendLog(Log.CreateLog(String.Format("ORP captured : {0}", capturedValue), LogType.Information));
                                        var status = sensorManager.GetSensorStatus(SensorTypeEnum.ORP, userSettings.isSleepModeEnable);
                                        lg.AppendLog(Log.CreateLog(String.Format("ORP status : {0}", status), LogType.System));
                                    }
                                }
                                else
                                    lg.AppendLog(Log.CreateLog(String.Format("ORP value out of range - {0}", capturedValue), LogType.Warning));
                            }  

                            if (sensorManager.isSensorOnline(SensorTypeEnum.dissolvedOxygen))
                            {
                                if (userSettings.isDiagnosticModeEnable)
                                  lg.AppendLog(Log.CreateLog("DO capturing", LogType.Information));

                                decimal capturedValue = 0;
                                capturedValue = sensorManager.RecordSensorsMeasure(SensorTypeEnum.dissolvedOxygen, userSettings.isSleepModeEnable);

                                if (capturedValue > 0 && capturedValue < 100)
                                {
                                    var task = Task.Run(async () =>
                                    {
                                        await databaseModel.AddMesure(captureDateTime, capturedValue, SensorTypeEnum.dissolvedOxygen);
                                    });
                                    task.Wait();

                                    if (userSettings.isDiagnosticModeEnable)
                                    {
                                        lg.AppendLog(Log.CreateLog(String.Format("DO captured : {0}", capturedValue), LogType.Information));
                                        var status = sensorManager.GetSensorStatus(SensorTypeEnum.dissolvedOxygen, userSettings.isSleepModeEnable);
                                        lg.AppendLog(Log.CreateLog(String.Format("DO status : {0}", status), LogType.System));
                                    }
                                }
                                else
                                lg.AppendLog(Log.CreateLog(String.Format("DO value out of range - {0}", capturedValue), LogType.Warning));
                            }

                        if (sensorManager.isSensorOnline(SensorTypeEnum.EC))
                        {
                            if (userSettings.isDiagnosticModeEnable)
                                lg.AppendLog(Log.CreateLog("EC capturing", LogType.Information));

                            decimal capturedValue = 0;
                            capturedValue = sensorManager.RecordSensorsMeasure(SensorTypeEnum.EC, userSettings.isSleepModeEnable);

                            if (capturedValue > 0)
                            {
                                var task = Task.Run(async () =>
                                {
                                    await databaseModel.AddMesure(captureDateTime, capturedValue, SensorTypeEnum.EC);
                                });
                                task.Wait();

                                if (userSettings.isDiagnosticModeEnable)
                                {
                                    lg.AppendLog(Log.CreateLog(String.Format("EC captured : {0}", capturedValue), LogType.Information));
                                    var status = sensorManager.GetSensorStatus(SensorTypeEnum.EC, userSettings.isSleepModeEnable);
                                    lg.AppendLog(Log.CreateLog(String.Format("EC status : {0}", status), LogType.System));
                                }
                            }
                            else
                                lg.AppendLog(Log.CreateLog(String.Format("EC value out of range - {0}", capturedValue), LogType.Warning));
                        }

                        if (userSettings.isTempHumiditySensorEnable)
                            {
                                try
                                {
                                    if (userSettings.isDiagnosticModeEnable)
                                        lg.AppendLog(Log.CreateLog("Air Temperature capturing", LogType.Information));

                                        decimal capturedValue = 0;
                                        Task.Run(async() => 
                                        {
                                            capturedValue = (decimal)humTempManager.Temperature;
                                            await Task.Delay(1000);
                                        }).Wait();                                 

                                if (capturedValue > 0 && capturedValue < 100)
                                {
                                    var taskTemp = Task.Run(async () =>
                                    {
                                        await databaseModel.AddMesure(captureDateTime, capturedValue, SensorTypeEnum.airTemperature);
                                    });
                                    taskTemp.Wait();

                                    if (userSettings.isDiagnosticModeEnable)
                                        lg.AppendLog(Log.CreateLog(String.Format("Air Temperature captured : {0}", capturedValue), LogType.Information));
                                }
                                else
                                    lg.AppendLog(Log.CreateLog(String.Format("Air Temperature out of range - {0}", capturedValue), LogType.Warning));
                                }
                                catch (Exception ex)
                                {
                                    lg.AppendLog(Log.CreateErrorLog("Exception on Air Temperature Sensor", ex));
                                }

                                try
                                {
                                    if (userSettings.isDiagnosticModeEnable)
                                        lg.AppendLog(Log.CreateLog("Humidity capturing", LogType.Information));

                                        decimal capturedValue = 0;
                                        Task.Run(async() => 
                                        {
                                            capturedValue = (decimal)humTempManager.Humidity;
                                            await Task.Delay(1000);
                                        }).Wait(); 

                                if (capturedValue > 0 && capturedValue < 100)
                                {
                                    var taskHum = Task.Run(async () =>
                                    {
                                        await databaseModel.AddMesure(captureDateTime, capturedValue, SensorTypeEnum.humidity);
                                    });
                                    taskHum.Wait();

                                    if (userSettings.isDiagnosticModeEnable)
                                        lg.AppendLog(Log.CreateLog(String.Format("Air Humidity captured : {0}", capturedValue), LogType.Information));
                                }
                                else
                                    lg.AppendLog(Log.CreateLog(String.Format("Air Humidity out of range - {0}", capturedValue), LogType.Warning));
                                }
                                catch (Exception ex)
                                {
                                lg.AppendLog(Log.CreateErrorLog("Exception on Air Humidity Sensor", ex));
                                }
                            }

                           lg.AppendLog(Log.CreateLog(String.Format("Measures captured in {0} sec.", watchMesures.ElapsedMilliseconds / 1000), LogType.System));  
                        
                        if(userSettings.isSigFoxComEnable && sigfoxManager.isInitialized && TICKSPERCYCLE >= 600000)
                        {
                            watchMesures.Restart();

                            string sigFoxSignature = String.Empty;

                            var taskSig = Task.Run(async () =>
                            {
                                sigFoxSignature = await databaseModel.GetLastMesureSignature();
                            });
                            taskSig.Wait();

                            sigfoxManager.SendMessage(sigFoxSignature);

                            //if (userSettings.isDiagnosticModeEnable)
                            //    sigfoxManager.Listen();

                            lg.AppendLog(Log.CreateLog(String.Format("Data sent to Azure via Sigfox in {0} sec.", watchMesures.ElapsedMilliseconds / 1000), LogType.System));
                        }

                        if (!userSettings.isSigFoxComEnable && NetworkInterface.GetIsNetworkAvailable())
                        {
                            string sigFoxSignature = String.Empty;

                            var taskSig = Task.Run(async () =>
                            {
                                sigFoxSignature = await databaseModel.GetLastMesureSignature();
                            });
                            taskSig.Wait();

                            using (var client = new HttpClient())
                            {
                                var request = new Message()
                                {
                                    content = sigFoxSignature,
                                    device = userSettings.productionSiteId,
                                    date = DateTime.Now.ToString(),
                                };

                                var taskWeb = Task.Run(async () =>
                                {
                                    try
                                    {
                                       var response = await client.PostAsync(userSettings.hubMessageAPI,
                                       new StringContent(JsonSerializer.Serialize(request),
                                       Encoding.UTF8, "application/json"));

                                        if (response.IsSuccessStatusCode)
                                        {
                                            lg.AppendLog(Log.CreateLog("Measures sent to Azure via Internet", LogType.Information));
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        lg.AppendLog(Log.CreateErrorLog("Exception on Measures to Azure", ex));
                                    }
                                });

                                taskWeb.Wait();           
                            }
                        }

                        bw.ReportProgress(33);         
                    }
                }
                catch (Exception ex)
                {
                    lg.AppendLog(Log.CreateErrorLog("Exception on Measures", ex));
                }
            }
            watch.Stop();
        }
    }

}