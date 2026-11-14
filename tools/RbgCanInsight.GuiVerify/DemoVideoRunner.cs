using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using RbgCanInsight.App;
using RbgCanInsight.App.Common;
using RbgCanInsight.App.ViewModels;

internal static class DemoVideoRunner
{
    [STAThread]
    public static int Run(string[] args)
    {
        if (args.Length is not (1 or 5))
        {
            Console.Error.WriteLine("Usage: --record-demo <control-directory> [left top width height]");
            return 2;
        }

        var controlDirectory = Path.GetFullPath(args[0]);
        var left = args.Length == 5 ? double.Parse(args[1]) : 0;
        var top = args.Length == 5 ? double.Parse(args[2]) : 0;
        var width = args.Length == 5 ? double.Parse(args[3]) : 2560;
        var height = args.Length == 5 ? double.Parse(args[4]) : 1440;
        Directory.CreateDirectory(controlDirectory);
        var readyPath = Path.Combine(controlDirectory, "ready");
        var startPath = Path.Combine(controlDirectory, "start");
        var donePath = Path.Combine(controlDirectory, "done");
        File.Delete(readyPath);
        File.Delete(startPath);
        File.Delete(donePath);

        var tracePath = Path.Combine(controlDirectory, "synthetic-demo.asc");
        var dbcPath = Path.Combine(controlDirectory, "synthetic-demo.dbc");
        var odxPath = Path.Combine(controlDirectory, "synthetic-demo.odx");
        File.WriteAllText(tracePath, BuildDemoTrace());
        File.WriteAllText(dbcPath, DemoDbcFixture);
        File.WriteAllText(odxPath, DemoOdxFixture);

        AppSettingsStore.SaveOnboardingShown();
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/RbgCanInsight;component/Themes/Palette.Dark.xaml")
        });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/RbgCanInsight;component/Themes/Styles.xaml")
        });

        var window = new MainWindow
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Topmost = true,
            ShowInTaskbar = false
        };

        app.Startup += async (_, _) =>
        {
            window.Show();
            var model = (MainViewModel)window.DataContext;

            await model.LoadDbcAsync([dbcPath]);
            await model.LoadTraceAsync(tracePath);
            await model.Mapping.LoadCatalogsAsync([odxPath]);

            var coolant = model.Signals.EnsurePlotted("EngineData", "EngCoolantTemp");
            var load = model.Signals.EnsurePlotted("EngineData", "EngineLoad");
            var speed = model.Signals.EnsurePlotted("VehicleData", "VehicleSpeed");
            if (new[] { coolant, load, speed }.Any(series => series is null || series.Samples.Count == 0))
            {
                throw new InvalidOperationException("The synthetic demo did not produce all required plot series.");
            }

            model.Signals.IsCumulativeReveal = true;
            model.Signals.YAxisModeIndex = 0;
            model.Signals.FitAllCommand.Execute(null);
            model.Signals.HeadCommand.Execute(null);

            foreach (var row in model.Trace.Rows.Take(2))
            {
                row.IsExpanded = true;
            }

            model.Trace.SelectedRow = model.Trace.Rows.FirstOrDefault();
            model.SelectedDbcDatabase = model.DbcDatabases.FirstOrDefault();
            model.SelectedDbcMessage = model.DbcMessages.FirstOrDefault();
            model.SelectedNavIndex = 1;
            model.Trace.PlaybackSpeed = 4;
            await Pause(1500);
            File.WriteAllText(readyPath, "ready");

            while (!File.Exists(startPath))
            {
                await Pause(100);
            }

            await Pause(1800);
            model.Trace.TogglePlaybackCommand.Execute(null);
            await Pause(7000);
            model.Trace.TogglePlaybackCommand.Execute(null);

            model.SelectedNavIndex = 2;
            await Pause(6000);

            model.SelectedNavIndex = 3;
            model.Signals.PlaybackSpeed = 1;
            model.Signals.HeadCommand.Execute(null);
            model.Signals.TogglePlaybackCommand.Execute(null);
            await Pause(8500);
            model.Signals.TogglePlaybackCommand.Execute(null);
            coolant!.IsDetailOpen = true;
            await Pause(4500);
            coolant.IsDetailOpen = false;

            model.SelectedNavIndex = 8;
            model.Uds.SelectedViewIndex = 0;
            await Pause(4000);
            model.Uds.SelectedViewIndex = 1;
            await Pause(4000);
            model.Uds.SelectedViewIndex = 5;
            await Pause(4000);
            model.Uds.SelectedViewIndex = 7;
            await Pause(5500);
            model.Uds.SelectedViewIndex = 8;
            await Pause(5000);
            model.Uds.SelectedViewIndex = 9;
            await Pause(5000);
            model.Uds.SelectedViewIndex = 10;
            await Pause(5000);

            model.SelectedNavIndex = 4;
            model.Matrix.SelectedRow = model.Matrix.Rows.FirstOrDefault(row => row.ChangeCount > 0);
            await Pause(6000);

            model.SelectedNavIndex = 5;
            model.ByteMatrix.SelectedRow = model.ByteMatrix.Rows.FirstOrDefault();
            if (model.ByteMatrix.SelectedRow is not null)
            {
                model.ByteMatrix.SelectedRow.IsDetailOpen = true;
            }

            await Pause(4500);
            model.ByteMatrix.PlaybackSpeed = 2;
            model.ByteMatrix.TogglePlaybackCommand.Execute(null);
            await Pause(6500);
            model.ByteMatrix.TogglePlaybackCommand.Execute(null);

            model.SelectedNavIndex = 6;
            model.SelectedStatisticsRow = model.StatisticsRows.FirstOrDefault();
            await Pause(6500);

            model.SelectedNavIndex = 1;
            await Pause(6500);
            File.WriteAllText(donePath, "done");
            window.Close();
            app.Shutdown();
        };

        app.Run();
        return 0;
    }

    private static async Task Pause(int milliseconds)
    {
        await Task.Delay(milliseconds);
    }

    private static string BuildDemoTrace()
    {
        var text = new StringBuilder();
        text.AppendLine("date Tue Aug 11 10:00:00.000 2026");
        text.AppendLine("base hex  timestamps absolute");
        text.AppendLine("internal events logged");

        const int sampleCount = 1200;
        for (var index = 0; index < sampleCount; index++)
        {
            var time = index * 0.05;
            var phase = index / 24d;

            var engine = new byte[8];
            engine[0] = ToByte(82 + (18 * Math.Sin(phase / 8)) + 40);
            engine[1] = ToByte((52 + (38 * Math.Sin(phase))) / 0.5);
            engine[2] = ToByte(91 + (12 * Math.Sin(phase / 11)) + 40);
            engine[3] = ToByte((13.6 + (0.5 * Math.Sin(phase / 3))) / 0.1);
            AppendFrame(text, time, 0x100, engine);

            var powertrain = new byte[8];
            SetUInt16(powertrain, 0, (int)((900 + (2700 * (0.5 + (0.5 * Math.Sin(phase / 2))))) / 0.25));
            powertrain[2] = ToByte((48 + (42 * Math.Sin(phase / 1.7))) / 0.4);
            powertrain[3] = (byte)(1 + ((index / 120) % 6));
            SetUInt16(powertrain, 4, (int)(220 + (80 * Math.Sin(phase / 5))));
            AppendFrame(text, time + 0.0005, 0x300, powertrain);

            if (index % 2 == 0)
            {
                var vehicle = new byte[8];
                SetUInt16(vehicle, 0, (int)((62 + (48 * Math.Sin(phase / 4))) / 0.01));
                SetUInt16(vehicle, 2, (index * 3) % 60_000);
                vehicle[4] = ToByte(42 + (35 * Math.Sin(phase / 2.5)));
                AppendFrame(text, time + 0.0010, 0x200, vehicle);

                var chassis = new byte[8];
                SetInt16(chassis, 0, (int)((110 * Math.Sin(phase / 1.3)) / 0.1));
                SetUInt16(chassis, 2, (int)((24 + (20 * Math.Max(0, Math.Sin(phase / 2.2)))) / 0.1));
                SetInt16(chassis, 4, (int)((0.7 * Math.Sin(phase / 1.1)) / 0.001));
                AppendFrame(text, time + 0.0015, 0x400, chassis);
            }

            if (index % 5 == 0)
            {
                var thermal = new byte[8];
                thermal[0] = ToByte(31 + (9 * Math.Sin(phase / 7)) + 40);
                thermal[1] = ToByte(44 + (7 * Math.Sin(phase / 9)) + 40);
                thermal[2] = ToByte((46 + (40 * Math.Max(0, Math.Sin(phase / 3)))) / 0.5);
                thermal[3] = ToByte(88 + (8 * Math.Sin(phase / 10)) + 40);
                AppendFrame(text, time + 0.0020, 0x500, thermal);
            }

            if (index % 100 == 20)
            {
                AppendDiagnosticCycle(text, time + 0.0100, index / 100);
            }
        }

        return text.ToString();
    }

    private static void AppendDiagnosticCycle(StringBuilder text, double time, int cycle)
    {
        switch (cycle % 4)
        {
            case 0:
                AppendFrame(text, time, 0x7E0, [0x03, 0x22, 0xF1, 0x90, 0, 0, 0, 0]);
                AppendFrame(text, time + 0.004, 0x7E8, [0x05, 0x62, 0xF1, 0x90, 0, (byte)(0x50 + cycle), 0, 0]);
                break;
            case 1:
                AppendFrame(text, time, 0x7E0, [0x03, 0x22, 0xF4, 0x05, 0, 0, 0, 0]);
                AppendFrame(text, time + 0.004, 0x7E8, [0x04, 0x62, 0xF4, 0x05, (byte)(0x32 + cycle), 0, 0, 0]);
                break;
            case 2:
                AppendFrame(text, time, 0x7E0, [0x03, 0x19, 0x02, 0xFF, 0, 0, 0, 0]);
                AppendFrame(text, time + 0.004, 0x7E8, [0x07, 0x59, 0x02, 0xFF, 0x12, 0x34, (byte)(0x50 + cycle), 0x0D]);
                break;
            default:
                AppendFrame(text, time, 0x7E0, [0x05, 0x31, 0x01, 0x02, 0x03, (byte)cycle, 0, 0]);
                AppendFrame(text, time + 0.003, 0x7E8, [0x03, 0x7F, 0x31, 0x78, 0, 0, 0, 0]);
                AppendFrame(text, time + 0.008, 0x7E8, [0x05, 0x71, 0x01, 0x02, 0x03, 0, 0, 0]);
                break;
        }
    }

    private static void AppendFrame(StringBuilder text, double time, int identifier, byte[] data)
    {
        text.Append(time.ToString("F6", CultureInfo.InvariantCulture));
        text.Append(" 1 ");
        text.Append(identifier.ToString("X", CultureInfo.InvariantCulture));
        text.Append(" Rx d 8 ");
        text.AppendLine(string.Join(' ', data.Select(value => value.ToString("X2", CultureInfo.InvariantCulture))));
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, byte.MaxValue);

    private static void SetUInt16(byte[] data, int offset, int value)
    {
        var bounded = Math.Clamp(value, ushort.MinValue, ushort.MaxValue);
        data[offset] = (byte)bounded;
        data[offset + 1] = (byte)(bounded >> 8);
    }

    private static void SetInt16(byte[] data, int offset, int value)
    {
        var bounded = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        data[offset] = (byte)bounded;
        data[offset + 1] = (byte)(bounded >> 8);
    }

    private const string DemoDbcFixture = """
        VERSION ""

        BO_ 256 EngineData: 8 ECM
         SG_ EngCoolantTemp : 0|8@1+ (1,-40) [-40|200] "degC" Vector__XXX
         SG_ EngineLoad : 8|8@1+ (0.5,0) [0|100] "%" Vector__XXX
         SG_ OilTemp : 16|8@1+ (1,-40) [-40|200] "degC" Vector__XXX
         SG_ BatteryVoltage : 24|8@1+ (0.1,0) [0|25.5] "V" Vector__XXX

        BO_ 512 VehicleData: 8 BCM
         SG_ VehicleSpeed : 0|16@1+ (0.01,0) [0|250] "km/h" Vector__XXX
         SG_ OdometerDelta : 16|16@1+ (0.1,0) [0|6553.5] "m" Vector__XXX
         SG_ BrakePedal : 32|8@1+ (1,0) [0|100] "%" Vector__XXX

        BO_ 768 PowertrainStatus: 8 PCM
         SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
         SG_ ThrottlePosition : 16|8@1+ (0.4,0) [0|100] "%" Vector__XXX
         SG_ SelectedGear : 24|4@1+ (1,0) [0|15] "" Vector__XXX
         SG_ TorqueEstimate : 32|16@1+ (1,0) [0|65535] "Nm" Vector__XXX

        BO_ 1024 ChassisStatus: 8 CHASSIS
         SG_ SteeringAngle : 0|16@1- (0.1,0) [-780|780] "deg" Vector__XXX
         SG_ BrakePressure : 16|16@1+ (0.1,0) [0|250] "bar" Vector__XXX
         SG_ LateralAccel : 32|16@1- (0.001,0) [-20|20] "m/s2" Vector__XXX

        BO_ 1280 ThermalStatus: 8 THERMAL
         SG_ IntakeAirTemp : 0|8@1+ (1,-40) [-40|200] "degC" Vector__XXX
         SG_ FuelTemp : 8|8@1+ (1,-40) [-40|200] "degC" Vector__XXX
         SG_ FanDuty : 16|8@1+ (0.5,0) [0|100] "%" Vector__XXX
         SG_ TransmissionTemp : 24|8@1+ (1,-40) [-40|200] "degC" Vector__XXX
        """;

    private const string DemoOdxFixture = """
        <ODX>
          <DIAG-LAYER-CONTAINER><SHORT-NAME>SyntheticOfflineDemo</SHORT-NAME></DIAG-LAYER-CONTAINER>
          <ECU-VARIANT><SHORT-NAME>DemoECU</SHORT-NAME></ECU-VARIANT>
          <REQUEST ID="REQ_F405">
            <PARAM SEMANTIC="SERVICE-ID"><CODED-VALUE>34</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
            <PARAM SEMANTIC="ID"><CODED-VALUE>62469</CODED-VALUE><DIAG-CODED-TYPE><BIT-LENGTH>16</BIT-LENGTH></DIAG-CODED-TYPE></PARAM>
          </REQUEST>
          <POS-RESPONSE ID="RESP_F405"><PARAM SEMANTIC="DATA"><DOP-REF ID-REF="DOP_TEMP" /></PARAM></POS-RESPONSE>
          <DATA-OBJECT-PROP ID="DOP_TEMP">
            <SHORT-NAME>EngineCoolantTemperature</SHORT-NAME>
            <DIAG-CODED-TYPE BASE-DATA-TYPE="A_UINT8"><BIT-LENGTH>8</BIT-LENGTH></DIAG-CODED-TYPE>
            <PHYSICAL-TYPE BASE-DATA-TYPE="A_FLOAT64" />
            <COMPU-METHOD><COMPU-INTERNAL-TO-PHYS><COMPU-SCALES><COMPU-SCALE><COMPU-RATIONAL-COEFFS><COMPU-NUMERATOR><V>-40</V><V>1</V></COMPU-NUMERATOR></COMPU-RATIONAL-COEFFS><LOWER-LIMIT>-40</LOWER-LIMIT><UPPER-LIMIT>215</UPPER-LIMIT></COMPU-SCALE></COMPU-SCALES></COMPU-INTERNAL-TO-PHYS></COMPU-METHOD>
            <UNIT-REF ID-REF="UNIT_C" />
          </DATA-OBJECT-PROP>
          <UNIT ID="UNIT_C"><SHORT-NAME>degC</SHORT-NAME><DISPLAY-NAME>degC</DISPLAY-NAME></UNIT>
          <DIAG-SERVICE>
            <SHORT-NAME>Read_EngineCoolantTemperature</SHORT-NAME><LONG-NAME>Engine coolant temperature</LONG-NAME>
            <REQUEST-REF ID-REF="REQ_F405" /><POS-RESPONSE-REF ID-REF="RESP_F405" />
          </DIAG-SERVICE>
          <DTC><SHORT-NAME>SensorPlausibility</SHORT-NAME><TROUBLE-CODE>1193046</TROUBLE-CODE><DISPLAY-TROUBLE-CODE>Sensor plausibility</DISPLAY-TROUBLE-CODE></DTC>
        </ODX>
        """;

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RbgCanInsight.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
