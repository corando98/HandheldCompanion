using ControllerCommon;
using ControllerCommon.Managers;
using ControllerCommon.Processor;
using ControllerCommon.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Timers;
using Windows.ApplicationModel.Store;
using Timer = System.Timers.Timer;
using static ControllerCommon.Utils.CommonUtils;

namespace HandheldCompanion.Managers
{
    public static class PowerMode
    {
        /// <summary>
        /// Better Battery mode.
        /// </summary>
        public static Guid BetterBattery = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");

        /// <summary>
        /// Better Performance mode.
        /// </summary>
        // public static Guid BetterPerformance = new Guid("3af9B8d9-7c97-431d-ad78-34a8bfea439f");
        public static Guid BetterPerformance = new Guid("00000000-0000-0000-0000-000000000000");

        /// <summary>
        /// Best Performance mode.
        /// </summary>
        public static Guid BestPerformance = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        public static List<Guid> PowerModes = new() { BetterBattery, BetterPerformance, BestPerformance };
    }

    public class PerformanceManager : Manager
    {
        #region imports
        /// <summary>
        /// Retrieves the active overlay power scheme and returns a GUID that identifies the scheme.
        /// </summary>
        /// <param name="EffectiveOverlayPolicyGuid">A pointer to a GUID structure.</param>
        /// <returns>Returns zero if the call was successful, and a nonzero value if the call failed.</returns>
        [DllImportAttribute("powrprof.dll", EntryPoint = "PowerGetEffectiveOverlayScheme")]
        private static extern uint PowerGetEffectiveOverlayScheme(out Guid EffectiveOverlayPolicyGuid);

        /// <summary>
        /// Sets the active power overlay power scheme.
        /// </summary>
        /// <param name="OverlaySchemeGuid">The identifier of the overlay power scheme.</param>
        /// <returns>Returns zero if the call was successful, and a nonzero value if the call failed.</returns>
        [DllImportAttribute("powrprof.dll", EntryPoint = "PowerSetActiveOverlayScheme")]
        private static extern uint PowerSetActiveOverlayScheme(Guid OverlaySchemeGuid);
        #endregion

        private Processor processor;

        // timers
        private Timer powerWatchdog;

        private Timer cpuWatchdog;
        protected object cpuLock = new();
        private bool cpuWatchdogPendingStop;

        private Timer gfxWatchdog;
        protected object gfxLock = new();
        private bool gfxWatchdogPendingStop;

        private Timer sensorWatchdog;
        protected object sensorLock = new();
        private bool sensorWatchdogPendingStop;

        private Timer AutoTDPWatchdog;
        protected object AutoTDPLock = new();
        private bool AutoTDPWatchdogPendingStop;

        private const short INTERVAL_DEFAULT = 1000;            // default interval between value scans
        private const short INTERVAL_INTEL = 5000;              // intel interval between value scans
        private const short INTERVAL_DEGRADED = 1000;          // degraded interval between value scans
        private const short INTERVAL_SENSOR = 100;
        private const short INTERVAL_AUTO_TDP = 100;

        public event LimitChangedHandler PowerLimitChanged;
        public delegate void LimitChangedHandler(PowerType type, int limit);

        public event ValueChangedHandler PowerValueChanged;
        public delegate void ValueChangedHandler(PowerType type, float value);

        public event StatusChangedHandler ProcessorStatusChanged;
        public delegate void StatusChangedHandler(bool CanChangeTDP, bool CanChangeGPU);

        // TDP limits
        private double[] FallbackTDP = new double[3];   // used to store fallback TDP
        private double[] StoredTDP = new double[3];     // used to store TDP
        private double[] CurrentTDP = new double[5];    // used to store current TDP
        
        
        private double TDPSetpoint = 10.0;
        double FPSRatio = 1.0;
        private double TDPSetpointInterpolator = 10.0f;
        private double TDPSetpointDerivative = 0.0f;
        private double ProcessValuePrevious;
        private double WantedFPSPrevious;
        private double PerformanceCurveError = 0;
        // Hardcode performance curve of Ghostrunner
        private double[,] PerformanceCurve = new double[,] {  { 5, 13.766, 0 }, { 6, 15.366, 0 }, { 7, 23.533, 0 }, { 8, 33.4666, 0 }, { 9, 43.5000, 0 }, { 10, 50.43, 0 }, { 11, 53.166, 0 }, { 12, 58.766, 0 }, { 13, 61.566, 0 }, { 14, 64.233, 0 }, { 15, 66.866, 0 }, { 16, 68.10, 0 }, { 17, 69.666, 0 }, { 18, 69.10, 0 }, { 19, 70.166, 0 }, { 20, 70.73, 0 },{ 21, 70.73, 0 }, { 22, 71.033, 0 }, { 23, 71.1000, 0 }, { 24, 71.733, 0 }, { 25, 72.366, 0 } };
        // Hardcode performance curve of Kena
        //private double[,] PerformanceCurve = new double[,] {  { 5, 6.033 }, { 6, 7 }, { 7, 10.167 }, { 8, 16.000 }, { 9, 19.100 }, { 10, 21.967 }, { 11, 24.700 }, { 12, 26.933 }, { 13, 28.033 }, { 14, 29.533 }, { 15, 31.000 }, { 16, 31.767 }, { 17, 32.700 }, { 18, 33.267 }, { 19, 33.900 }, { 20, 34.233 },{ 21, 34.767 }, { 22, 35.10 }, { 23, 35.55 }, { 24, 36.000 }, { 25, 36.367 } };
        private int TestRoundCounter = 1;
        private int TestStepCounter = 0;
        private double TDPSetpointPrevious = 0;
        double[] FPSMeasuredRound1 = new double[21];
        double[] FPSMeasuredRound2 = new double[21];
        double[] FPSMeasuredRound3 = new double[21];

        // Auto TDP
        private short AutoTDPState = 0;
        private const short AUTO_TDP_STATE_IDLE = 0;
        private const short AUTO_TDP_STATE_CO_BIAS = 10;
        private const short AUTO_TDP_STATE_PID_CONTROL = 20;

        public OneEuroFilter3D FPSActualFiltered = new();
        public OneEuroFilter3D TDPActualFiltered = new();
        double FPSActualFilteredValue;
        double TDPActualFilteredValue;
        double MaxTDP = 25;
        double MinTDP = 5;
        double FPSSetpointPrevious;
        double COBias = 0;
        private short COBiasAttemptCounter;
        private short COBiasAttemptAmount = 3;
        private short COBiasAttemptTimeoutMilliSec;
        private short FPSResponseTimeMilliSec = 1300;
        private double PTermEnabled = 1;
        private double ITermEnabled = 1;
        private double ITerm = 0;
        private double ITermPrev = 0;
        public static Stopwatch stopwatch;
        public static double TotalMilliseconds;
        public static double UpdateTimePreviousMilliseconds;
        public static double DeltaMilliSeconds = -100;
        double DTermEnabled = 0;
        double[] TDPSetpointHistory = new double[20];
        double TDPSetpointValid = 10.0;

        // GPU limits
        private double FallbackGfxClock;
        private double StoredGfxClock;
        private double CurrentGfxClock;

        // Power modes
        private Guid RequestedPowerMode;

        public PerformanceManager() : base()
        {
            // initialize timer(s)
            powerWatchdog = new Timer() { Interval = INTERVAL_DEFAULT, AutoReset = true, Enabled = false };
            powerWatchdog.Elapsed += powerWatchdog_Elapsed;

            cpuWatchdog = new Timer() { Interval = INTERVAL_DEFAULT, AutoReset = true, Enabled = false };
            cpuWatchdog.Elapsed += cpuWatchdog_Elapsed;

            gfxWatchdog = new Timer() { Interval = INTERVAL_DEFAULT, AutoReset = true, Enabled = false };
            gfxWatchdog.Elapsed += gfxWatchdog_Elapsed;

            sensorWatchdog = new Timer() { Interval = INTERVAL_SENSOR, AutoReset = true, Enabled = true };
            sensorWatchdog.Elapsed += sensorWatchdog_Elapsed;

            AutoTDPWatchdog = new Timer() { Interval = INTERVAL_AUTO_TDP, AutoReset = true, Enabled = false };
            AutoTDPWatchdog.Elapsed += AutoTDP_Elapsed;

            double FPSActualFilterMinCutoff = 0.15;
            double FPSActualFilterBeta = 0.1;
            FPSActualFiltered.SetFilterAttrs(FPSActualFilterMinCutoff, FPSActualFilterBeta);

            double TDPActualFilterMinCutoff = 0.25; 
            double TDPActualFilterBeta = 0.2; 
            TDPActualFiltered.SetFilterAttrs(TDPActualFilterMinCutoff, TDPActualFilterBeta);

            // initialize stopwatch
            stopwatch = new Stopwatch();

            ProfileManager.Applied += ProfileManager_Applied;
            ProfileManager.Updated += ProfileManager_Updated;
            ProfileManager.Discarded += ProfileManager_Discarded;

            // initialize settings
            double TDPdown = SettingsManager.GetDouble("QuickToolsPerformanceTDPSustainedValue");
            double TDPup = SettingsManager.GetDouble("QuickToolsPerformanceTDPBoostValue");
            double GPU = SettingsManager.GetDouble("QuickToolsPerformanceGPUValue");

            // request TDP(s)
            RequestTDP(PowerType.Slow, TDPdown);
            RequestTDP(PowerType.Stapm, TDPdown);
            RequestTDP(PowerType.Fast, TDPup);

            // request GPUclock
            if (GPU != 0)
                RequestGPUClock(GPU, true);
        }

        private void ProfileManager_Updated(Profile profile, ProfileUpdateSource source, bool isCurrent)
        {
            if (!isCurrent)
                return;

            ProfileManager_Applied(profile);
        }

        private void ProfileManager_Discarded(Profile profile, bool isCurrent)
        {
            if (!isCurrent)
                return;

            // restore user defined TDP
            RequestTDP(FallbackTDP, false);

            // stop cpuWatchdog if system settings is disabled
            bool cpuWatchdogState = SettingsManager.GetBoolean("QuickToolsPerformanceTDPEnabled");
            if (profile.TDP_override && !cpuWatchdogState)
                StopTDPWatchdog();
        }

        private void ProfileManager_Applied(Profile profile)
        {
            // start cpuWatchdog if system settings is disabled
            bool cpuWatchdogState = SettingsManager.GetBoolean("QuickToolsPerformanceTDPEnabled");
            if (profile.TDP_override && !cpuWatchdogState)
                StartTDPWatchdog();

            // apply profile defined TDP
            if (profile.TDP_override && profile.TDP_value is not null)
                RequestTDP(profile.TDP_value, false);
            else
                RequestTDP(FallbackTDP, false); // redudant with ProfileManager_Discarded ?
        }

        private void powerWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
        {
            // Checking if active power shceme has changed
            if (PowerGetEffectiveOverlayScheme(out Guid activeScheme) == 0)
                if (activeScheme != RequestedPowerMode)
                    PowerSetActiveOverlayScheme(RequestedPowerMode);
        }

        private void cpuWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (processor is null || !processor.IsInitialized)
                return;

            if (Monitor.TryEnter(cpuLock))
            {
                bool TDPdone = false;
                bool MSRdone = false;

                double WantedFPS = SettingsManager.GetDouble("QuickToolsPerformanceAutoTDPFPSValue");
                int algo_choice = 6;

                if (algo_choice == 1)
                {


                    if (HWiNFOManager.process_value_tdp_actual != 0.0 && HWiNFOManager.process_value_fps != 0.0 && HWiNFOManager.process_value_fps != 0.0)
                    {
                        TDPSetpoint = TDPSetpoint + (TDPSetpoint / HWiNFOManager.process_value_fps) * (WantedFPS - HWiNFOManager.process_value_fps);
                    }
                }
                else if (algo_choice == 2)
                {

                    if (HWiNFOManager.process_value_tdp_actual == 0.0 || HWiNFOManager.process_value_fps == 0.0 || HWiNFOManager.process_value_fps == 0.0)
                    {
                        return;
                    }

                    int i;
                    double ExpectedFPS = 0.0f;
                    int NodeAmount = 21;
                    double DeltaError = 0.0f;
                    double ProcessValueNew = 0.0f;
                    double DTerm = 0.0f;
                    double DeltaTimeSec = INTERVAL_DEFAULT / 1000; // @todo, replace with better measured timer 
                    double DFactor = -0.07; // 0.09 caused issues at 30 FPS, 0.18 goes even more unstable
                    double DTermEnabled = 1;
                    TDPSetpoint = Math.Clamp(TDPSetpoint, 5, 25);

                    //LogManager.LogInformation("NodeAmount {0} ", NodeAmount);

                    // Convert xy list to separate single lists
                    double[] X = new double[NodeAmount];
                    double[] Y = new double[NodeAmount];

                    for (int idx = 0; idx < NodeAmount; idx++)
                    {
                        X[idx] = PerformanceCurve[idx, 0];
                        Y[idx] = PerformanceCurve[idx, 1];
                    }

                    //LogManager.LogInformation("X {0} ", X);
                    //LogManager.LogInformation("Y {0} ", Y);

                    // Check performance curve for current TDP and corresponding expected FPS
                    // Use actual FPS for current TDP setpoint

                    // Figure out between which two nodes the current TDP setpoint is
                    i = Array.FindIndex(X, k => Math.Clamp(TDPSetpoint, 5, 25) <= k);

                    if (i == -1)
                    {
                        LogManager.LogInformation("Array.FindIndex out of bounds for TDPSetpoint of: {0:000}", TDPSetpoint);
                        return;
                    }

                    // Interpolate between those two points
                    ExpectedFPS = Y[i - 1] + (TDPSetpoint - X[i - 1]) * (Y[i] - Y[i - 1]) / (X[i] - X[i - 1]);

                    //LogManager.LogInformation("For TDPSetpoint {0:0.000} we have ExpectedFPS {1:0.000} ", TDPSetpoint, ExpectedFPS);

                    // Determine ratio difference between expected FPS and actual
                    FPSRatio = (HWiNFOManager.process_value_fps / ExpectedFPS);

                    //LogManager.LogInformation("FPSRatio {0:0.000} = ExpectedFPS {1:0.000} / ActualFPS {2:0.000}", FPSRatio, ExpectedFPS, HWiNFOManager.process_value_fps);

                    // Update whole performance curve FPS values
                    for (int idx = 0; idx < NodeAmount; idx++)
                    {
                        // @todo, instead of the first interpolation, we could use divide by performance curve error 
                        // apparantly... huh?!

                        PerformanceCurveError = WantedFPSPrevious / HWiNFOManager.process_value_fps;

                        double ScalingDamper = 0.93; // 0.95 too slow 0.96 seems ok

                        if (FPSRatio >= 1)
                        {
                            if (ScalingDamper < 1.0) { FPSRatio = ((FPSRatio - 1) * ScalingDamper) + 1; }
                            if (ScalingDamper >= 1.0) { FPSRatio = ((FPSRatio - 1) / ScalingDamper) + 1; }
                        }
                        else
                        {
                            if (ScalingDamper < 1.0) { FPSRatio = 1 - ((1 - FPSRatio) * ScalingDamper); }
                            if (ScalingDamper >= 1.0) { FPSRatio = 1 - ((1 - FPSRatio) / ScalingDamper); }
                        }

                        PerformanceCurve[idx, 1] = PerformanceCurve[idx, 1] * FPSRatio;
                        Y[idx] = PerformanceCurve[idx, 1];
                    }

                    //LogManager.LogInformation("Updated curve:");
                    //LogManager.LogInformation("X {0} ", X);
                    //LogManager.LogInformation("Y {0} ", Y);

                    // Check performance curve for new TDP required for requested FPS
                    // cautious of limits, 
                    //if highest FPS in performance curve is lower then requested FPS, set TDP max
                    if (Y[NodeAmount - 1] < WantedFPS)
                    {
                        TDPSetpoint = 25.0f;
                    }
                    //if lowest FPS in performance curve is higher then requested FPS, set TDP min
                    else if (Y[0] > WantedFPS)
                    {
                        TDPSetpoint = 5.0f;
                    }
                    else
                    {
                        // Figure out between which two nodes the wanted FPS is
                        i = Array.FindIndex(Y, k => WantedFPS <= k);

                        // Interpolate between those two points
                        TDPSetpointInterpolator = X[i - 1] + (WantedFPS - Y[i - 1]) * (X[i] - X[i - 1]) / (Y[i] - Y[i - 1]);
                        //LogManager.LogInformation("For WantedFPS {0:0.0} we have interpolated TDPSetpoint {1:0.000} ", WantedFPS, TDPSetpoint);

                        // (PI)D derivate control component to dampen
                        // @ next step idea, add derivate but be careful with kick: https://blog.mbedded.ninja/programming/general/pid-control/
                        ProcessValueNew = (float)HWiNFOManager.process_value_fps;

                        // First time around, initialise previous
                        if (ProcessValuePrevious == float.NaN) { ProcessValuePrevious = ProcessValueNew; }

                        DeltaError = ProcessValueNew - ProcessValuePrevious;
                        DTerm = DeltaError / DeltaTimeSec;
                        TDPSetpointDerivative = DFactor * DTerm;

                        //LogManager.LogInformation("Delta error {0:0.000} = ProcessValueNew {1:0.000} - ProcessValuePrev {2:0.000}", DeltaError, ProcessValueNew, ProcessValuePrevious);
                        //LogManager.LogInformation("D Term {0:0.00000} = DeltaError {1:0.000} / DeltaTime {2:0.000}", DTerm, DeltaError, DeltaTime);
                        //LogManager.LogInformation("D adds: {0:0.00000}", (DFactor * DTerm));

                        // For next loop
                        ProcessValuePrevious = ProcessValueNew;

                        // Add derivitate term to setpoint
                        TDPSetpoint = TDPSetpointInterpolator + TDPSetpointDerivative * DTermEnabled;

                        WantedFPSPrevious = WantedFPS;


                    }



                }
                else if (algo_choice == 3)
                {
                    if (HWiNFOManager.process_value_tdp_actual == 0.0 && HWiNFOManager.process_value_fps == 0.0 && HWiNFOManager.process_value_fps == 0.0)
                    {
                        return;
                    }

                    // Step responce function, small step response, print for plotting.
                    // Least squares fun: https://planetcalc.com/8735/ cubic regression with or without fixed cross points seems most worthwhile
                    int TDPMax = 25;
                    int TDPMin = 5;

                    if (TestRoundCounter == 1 && TestStepCounter > 0 && TestStepCounter < 22) { FPSMeasuredRound1[TestStepCounter - 1] = HWiNFOManager.process_value_fps; }
                    if (TestRoundCounter == 2 && TestStepCounter > 0 && TestStepCounter < 22) { FPSMeasuredRound2[TestStepCounter - 1] = HWiNFOManager.process_value_fps; }
                    if (TestRoundCounter == 3 && TestStepCounter > 0 && TestStepCounter < 22) { FPSMeasuredRound3[TestStepCounter - 1] = HWiNFOManager.process_value_fps; }

                    if (TestStepCounter == 22)
                    {
                        TestRoundCounter += 1;
                        TestStepCounter = 0;

                        if (TestRoundCounter == 4)
                        {
                            TestRoundCounter = 1;
                            // Print results of last 3 tests
                            // Header
                            LogManager.LogInformation(",TDP,FPS1,FPS2,FPS3,FPS Average");

                            // Content
                            for (int idx = 0; idx < 21; idx++)
                            {
                                LogManager.LogInformation(",{0:0},{1:0.000},{2:0.000},{3:0.000},{4:0.000}", idx + 5, FPSMeasuredRound1[idx], FPSMeasuredRound2[idx], FPSMeasuredRound3[idx], (FPSMeasuredRound1[idx] + FPSMeasuredRound2[idx] + FPSMeasuredRound3[idx]) / 3);
                            }

                            // Hardcoded performance curve output for C#
                            // Example
                            // private double[,] PerformanceCurve = new double[,] {  { 5, 15 }, { 6, 18 }, { 7, 25 }, { 8, 36 }, { 9, 46 }, { 10, 54 }, { 11, 59 }, { 12, 64 }, { 13, 68 }, { 14, 71 }, { 15, 74 }, { 16, 73}, { 17, 74 }, { 18, 75 }, { 19, 76 }, { 20, 80 },{ 21, 81 }, { 22, 81 }, { 23, 81 }, { 24, 82 }, { 25, 84 }};
                            string PerformanceCurveText = "private double[,] PerformanceCurve = new double[,] {";

                            for (int idx = 0; idx < 21; idx++)
                            {
                                PerformanceCurveText += " { " + idx + 5 + ", " + (FPSMeasuredRound1[idx] + FPSMeasuredRound2[idx] + FPSMeasuredRound3[idx]) / 3 + " },";
                            }

                            PerformanceCurveText += " };";

                            LogManager.LogInformation(PerformanceCurveText);

                        }
                    }

                    TDPSetpoint = TDPMin + TestStepCounter;

                    LogManager.LogInformation("TDPTester Round: {0:0} Step: {1:0} TDP Setpoint: {2:0.0} Measured FPS (from previous TDP): {3:0.000}", TestRoundCounter, TestStepCounter, TDPSetpoint, HWiNFOManager.process_value_fps);

                    // For next round
                    TestStepCounter += 1;
                    TDPSetpointPrevious = TDPSetpoint;
                }

                else if (algo_choice == 4)
                {

                    if (HWiNFOManager.process_value_tdp_actual == 0.0 || HWiNFOManager.process_value_fps == 0.0 || HWiNFOManager.process_value_fps == 0.0)
                    {
                        return;
                    }

                    double DeltaError = 0.0f;
                    double ProcessValueNew = 0.0f;
                    double DTerm = 0;
                    double DeltaTimeSec = INTERVAL_DEFAULT / 1000; // @todo, replace with better measured timer 
                    double DFactor = -0.07; // 0.09 caused issues at 30 FPS, 0.18 goes even more unstable

                    double COBiasUnused;

                    // Update timestamp
                    TotalMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    DeltaMilliSeconds = (TotalMilliseconds - UpdateTimePreviousMilliseconds);
                    UpdateTimePreviousMilliseconds = TotalMilliseconds;

                    // @@@ Todo, use 1300 msec older tdp setpoint
                    // @@@ todo, use filtered FPS actual
                    // Update performance curve for current scene
                    COBiasUnused = DetermineControllerOutputBias(WantedFPS,
                                                                 HWiNFOManager.process_value_fps,
                                                                 TDPSetpoint);

                    // Process gain
                    // https://controlguru.com/process-gain-is-the-how-far-variable/

                    // Update process gain for current performance curve
                    PerformanceCurve = DeterminePerformanceCurveControllerProcessGains(PerformanceCurve);

                    // Determine process gain for current FPS through interpolation
                    double ProcessGainKp = 1.0 * DetermineControllerProcessGain(PerformanceCurve, HWiNFOManager.process_value_fps);

                    // Process Time Constant
                    // https://controlguru.com/process-gain-is-the-how-fast-variable-2/
                    // 63% of delta PV for 10 FPS change
                    double ProcessTimeConstantTp = 0.9; // 900 milliseconds

                    // Dead Time
                    // https://controlguru.com/dead-time-is-the-how-much-delay-variable/
                    // TDP takes 0.3 seconds after setpoint change
                    // FPS takes 0.5 seconds after setpoint change
                    double DeadTimeThetap = 0.5; // 500 milliseconds

                    double ControllerError = WantedFPS - HWiNFOManager.process_value_fps; // for now, intentially unfiltered

                    // Closed Loop Time Constant
                    // https://controlguru.com/pi-control-of-the-heat-exchanger/
                    double ClosedLoopTimeConstantTc = 0.0;

                    // Aggressive
                    // Tc is the larger of 0.1·Tp or 0.8·Өp
                    // Tested, goes unstable, fast.
                    ClosedLoopTimeConstantTc = Math.Max(0.1 * ProcessTimeConstantTp, 0.8 * DeadTimeThetap);

                    // Moderate
                    // Tc is the larger of 1·Tp or 8·Өp
                    ClosedLoopTimeConstantTc = Math.Max(1 * ProcessTimeConstantTp, 8 * DeadTimeThetap);

                    // Conservative
                    // Tc is the larger of  10·Tp or 80·Өp
                    ClosedLoopTimeConstantTc = Math.Max(10 * ProcessTimeConstantTp, 80 * DeadTimeThetap);


                    // Controller Gain Kc
                    // P Only cntroller
                    // double Kc = (0.2 / Kp) * Math.Pow((ProcessTimeConstantTp / Thetap), 1.22);
                    // PI controller
                    // double ControllerGainKc = (1 / ProcessGainKp) * (ProcessTimeConstantTp / (DeadTimeThetap + ClosedLoopTimeConstantTc));
                    // PID Controller
                    // The IMC tuning correlations for the Dependent, Ideal (Non-Interacting) PID form are
                    // https://controlguru.com/pid-control-of-the-heat-exchanger/
                    double ControllerGainKc = (1 / ProcessGainKp) * ((ProcessTimeConstantTp + 0.5 * DeadTimeThetap) / (ClosedLoopTimeConstantTc + 0.5 * DeadTimeThetap));

                    //LogManager.LogInformation("Process Gain: {0:0.000} ControllerGainKc: {1:0.000} ClosedLoopTimeConstantTc: {2:0.000}", ProcessGainKp, ControllerGainKc, ClosedLoopTimeConstantTc);

                    // PI Controller
                    // https://controlguru.com/integral-action-and-pi-control/

                    // P term, proportional control component
                    // Restrict to -/+ 10 Watt TDP
                    double PTerm = Math.Clamp(ControllerGainKc * ControllerError, -10, 10) * PTermEnabled;

                    //LogManager.LogInformation("P Term: {0:0.000} Enabled: {1:0}", PTerm, PTermEnabled);

                    // I term, integral control component
                    // Reset Time Ti, Notice that reset time, Ti, is always set equal to
                    // the time constant of the process, regardless of desired controller activity.
                    // PI controller reset time
                    // double ResetTimeTi = ClosedLoopTimeConstantTc;
                    // PID controller reset time
                    double ResetTimeTi = ProcessTimeConstantTp + (0.5 * DeadTimeThetap);
                    // I = I + Ki*e*(t - t_prev)
                    double IntegralGainKi = ControllerGainKc / ResetTimeTi;
                    // @@@ Todo, fix wording/naming here
                    // integral = integral_prior + error * iteration_time
                    ITerm = ITermPrev + ControllerError * (DeltaMilliSeconds / 1000);
                    if (ITermEnabled == 0) { ITerm = 0; }
                    ITermPrev = ITerm;

                    double ITermFinal = Math.Clamp(IntegralGainKi * ITerm, -1 * (MaxTDP - MinTDP), MaxTDP - MinTDP);

                    //LogManager.LogInformation("I Parameters: ResetTimeTi {0:0.000} ControllerGainKc {1:0.000} Ki {2:0.000} DeltaTime {3:0.000} ControllerError {4:0.000} ITerm {5:0.000}", ResetTimeTi, ControllerGainKc, IntegralGainKi, DeltaMilliSeconds / 1000, ControllerError, ITerm);
                    //LogManager.LogInformation("I Term x Ki: {0:0.000} Enabled: {1:0}", ITermFinal, ITermEnabled);

                    // D term, derivate control component
                    ProcessValueNew = (float)HWiNFOManager.process_value_fps;

                    // First time around, initialise previous
                    if (ProcessValuePrevious == float.NaN) { ProcessValuePrevious = ProcessValueNew; }

                    // PID Controller DerivativeTimeTd
                    double DerivativeTimeTd = (ProcessTimeConstantTp * DeadTimeThetap) / ((2 * ProcessTimeConstantTp) + DeadTimeThetap);
                    DeltaError = ProcessValueNew - ProcessValuePrevious;
                    DTerm = ControllerGainKc * DerivativeTimeTd * (DeltaError / DeltaTimeSec);

                    //LogManager.LogInformation("Delta error {0:0.000} = ProcessValueNew {1:0.000} - ProcessValuePrev {2:0.000}", DeltaError, ProcessValueNew, ProcessValuePrevious);
                    //LogManager.LogInformation("D Term {0:0.00000} = DeltaError {1:0.000} / DeltaTime {2:0.000}", DTerm, DeltaError, DeltaTime);
                    //LogManager.LogInformation("D adds: {0:0.00000}", (DFactor * DTerm));

                    // For next loop
                    ProcessValuePrevious = ProcessValueNew;
                    if (DTermEnabled == 0) { DTerm = 0; ProcessValuePrevious = 0; }

                    TDPSetpoint = COBias + PTerm * PTermEnabled + ITermFinal * ITermEnabled + DTerm * DTermEnabled;

                    LogManager.LogInformation("TDPSet;;;;{0:0.0};{1:0.000};{2:0.0000};{3:0.0000};{4:0.0000};{5:0.0000};{6:0.0000}", WantedFPS, TDPSetpoint, COBias, PTerm, ITermFinal, DTerm, TDPSetpointDerivative, PerformanceCurveError, FPSRatio);
                }

                else if (algo_choice == 5)
                {

                    // In case we don't have usable data, skip this round.
                    if (HWiNFOManager.process_value_tdp_actual == 0.0 || HWiNFOManager.process_value_fps == 0.0 || HWiNFOManager.process_value_fps == 0.0)
                    {
                        return;
                    }

                    // @@@ Todo, use 1300 msec older tdp setpoint
                    // @@@ todo, use filtered FPS actual
                    // Update performance curve for current scene and determine new TDP setpoint

                    // Get latest from HWinfo FPS
                    // @@@ Todo, fix daisy chained senser interval delay from RTSS to HWInfo to HC,
                    // possibly 100 msec more recent data and thus earlier correction and thus better.
                    double ProcessValueFPS = HWiNFOManager.CurrentFPS();

                    TDPSetpointInterpolator = DetermineControllerOutputBias(WantedFPS,
                                                                            ProcessValueFPS,
                                                                            TDPSetpointValid);

                    // D term, derivate control component
                    // Used as a damper
                    double DFactor = -0.07; // 0.09 caused issues at 30 FPS, 0.18 goes even more unstable

                    // Delta error
                    // First time around, initialise previous
                    if (ProcessValuePrevious == float.NaN || ProcessValuePrevious == 0.0) { ProcessValuePrevious = ProcessValueFPS; }
                    double DeltaError = ProcessValueFPS - ProcessValuePrevious;
                    // For next loop
                    ProcessValuePrevious = ProcessValueFPS;

                    // Delta time, update timestamp
                    double DeltaTimeSec = (stopwatch.Elapsed.TotalMilliseconds - UpdateTimePreviousMilliseconds) / 1000;
                    UpdateTimePreviousMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

                    double DTerm = DeltaError / DeltaTimeSec;
                    TDPSetpointDerivative = DFactor * DTerm;

                    //LogManager.LogInformation("Delta error {0:0.000} = ProcessValueNew {1:0.000} - ProcessValuePrev {2:0.000}", DeltaError, ProcessValueNew, ProcessValuePrevious);
                    //LogManager.LogInformation("D Term {0:0.00000} = DeltaError {1:0.000} / DeltaTime {2:0.000}", DTerm, DeltaError, DeltaTime);
                    //LogManager.LogInformation("D adds: {0:0.00000}", (DFactor * DTerm));

                    // Temp disable
                    DTermEnabled = 0;

                    TDPSetpoint = TDPSetpointInterpolator + TDPSetpointDerivative * DTermEnabled;


                    LogManager.LogInformation("TDPSet;;;;{0:0.0};{1:0.000};{2:0.0000};{3:0.0000};{4:0.0000};{5:0.0000};{6:0.0000}", WantedFPS, TDPSetpoint, TDPSetpointValid, TDPSetpointInterpolator, TDPSetpointDerivative, ProcessValueFPS, FPSRatio);
                }

                else if (algo_choice == 6)
                {
                    // In case we don't have usable data, skip this round.
                    if (HWiNFOManager.process_value_tdp_actual == 0.0 || HWiNFOManager.process_value_fps == 0.0)
                    {
                        return;
                    }

                    double ProcessValueFPS = HWiNFOManager.CurrentFPS();
                    // Be realistic with expectd proces value
                    ProcessValueFPS = Math.Clamp(ProcessValueFPS, 1, 500);


                    // Todo, use predictive trend line on FPS
                    // Determine error amount
                    double ControllerError = WantedFPS - ProcessValueFPS; // for now, intentially unfiltered

                    // Clamp error amount that is correct in one cycle
                    // -5 +15, going lower always overshoots (not safe, leads to instability), going higher always undershoots (which is safe)
                    // Todo, adjust clamp in case of actual FPS being 2x requested FPS, menu's going to 300+ fps for example.
                    ControllerError = Math.Clamp(ControllerError, -5, 15);

                    // Based on FPS/TDP ratio, determine how much adjustment is needed
                    double TDPAdjustment = ControllerError * TDPSetpointValid / ProcessValueFPS;
                    // Going lower or higher, we need to reduce the amount of TDP by a factor... or not apparently?!
                    if (ControllerError < 0.0) { 
                        TDPAdjustment *= 1.0; 
                    }
                    else {
                        TDPAdjustment *= 1.0;
                    }

                    // Todo, If we're close to target for 5 seconds, start reduction ripple

                    // Determine final setpoint
                    TDPSetpoint += TDPAdjustment;
                    TDPSetpoint = Math.Clamp(TDPSetpoint, 5, 25);

                    // Log
                    LogManager.LogInformation("TDPSet;;;;{0:0.0};{1:0.000};{2:0.0000};{3:0.0000};{4:0.0000}", WantedFPS, TDPSetpoint, TDPSetpointValid, TDPAdjustment, ProcessValueFPS);
                }

                // HWiNFOManager.process_value_tdp_actual or set as ratio?

                // Update all stored TDP values
                StoredTDP[0] = StoredTDP[1] = StoredTDP[2] = Math.Clamp(TDPSetpoint,5,25);

                //LogManager.LogInformation("TDPSet;;;;{0:0.0000};{1:0.0};{2:0.000};{3:0.0000};{4:0.0000};{5:0.0000}", StoredTDP[0], WantedFPS, TDPSetpointInterpolator, TDPSetpointDerivative, PerformanceCurveError, FPSRatio);

                // read current values and (re)apply requested TDP if needed
                foreach (PowerType type in (PowerType[])Enum.GetValues(typeof(PowerType)))
                {
                    int idx = (int)type;

                    // skip msr
                    if (idx >= StoredTDP.Length)
                        break;

                    double TDP = StoredTDP[idx];

                    if (processor.GetType() == typeof(AMDProcessor))
                    {
                        // AMD reduces TDP by 10% when OS power mode is set to Best power efficiency
                        if (RequestedPowerMode == PowerMode.BetterBattery)
                            TDP = (int)Math.Truncate(TDP * 0.9);
                    }
                    else if (processor.GetType() == typeof(IntelProcessor))
                    {
                        // Intel doesn't have stapm
                        if (type == PowerType.Stapm)
                            continue;
                    }

                    double ReadTDP = CurrentTDP[idx];

                    // we're in degraded condition
                    if (ReadTDP == 0 || ReadTDP < byte.MinValue || ReadTDP > byte.MaxValue)
                        cpuWatchdog.Interval = INTERVAL_DEGRADED;
                    else
                        cpuWatchdog.Interval = INTERVAL_DEFAULT;

                    // only request an update if current limit is different than stored
                    if (ReadTDP != TDP)
                        processor.SetTDPLimit(type, TDP);
                    else
                        TDPdone = true;
                }

                // processor specific
                if (processor.GetType() == typeof(IntelProcessor))
                {
                    // not ready yet
                    if (CurrentTDP[(int)PowerType.MsrSlow] == 0 || CurrentTDP[(int)PowerType.MsrFast] == 0)
                    {
                        Monitor.Exit(cpuLock);
                        return;
                    }

                    int TDPslow = (int)StoredTDP[(int)PowerType.Slow];
                    int TDPfast = (int)StoredTDP[(int)PowerType.Fast];

                    // only request an update if current limit is different than stored
                    if (CurrentTDP[(int)PowerType.MsrSlow] != TDPslow ||
                        CurrentTDP[(int)PowerType.MsrFast] != TDPfast)
                        ((IntelProcessor)processor).SetMSRLimit(TDPslow, TDPfast);
                    else
                        MSRdone = true;
                }

                // user requested to halt cpu watchdog
                if (TDPdone && MSRdone && cpuWatchdogPendingStop)
                    cpuWatchdog.Stop();

                Monitor.Exit(cpuLock);
            }
        }

        private void gfxWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (processor is null || !processor.IsInitialized)
                return;

            if (Monitor.TryEnter(gfxLock))
            {
                bool GPUdone = false;

                if (processor.GetType() == typeof(AMDProcessor))
                {
                    // not ready yet
                    if (CurrentGfxClock == 0)
                    {
                        Monitor.Exit(gfxLock);
                        return;
                    }
                }
                else if (processor.GetType() == typeof(IntelProcessor))
                {
                    // not ready yet
                    if (CurrentGfxClock == 0)
                    {
                        Monitor.Exit(gfxLock);
                        return;
                    }
                }

                // not ready yet
                if (StoredGfxClock == 0)
                {
                    Monitor.Exit(gfxLock);
                    return;
                }

                // only request an update if current gfx clock is different than stored
                if (CurrentGfxClock != StoredGfxClock)
                    processor.SetGPUClock(StoredGfxClock);
                else
                    GPUdone = true;

                // user requested to halt gpu watchdog
                if (GPUdone && gfxWatchdogPendingStop)
                    gfxWatchdog.Stop();

                Monitor.Exit(gfxLock);
            }
        }

        private void sensorWatchdog_Elapsed(object? sender, ElapsedEventArgs e)
        {

            if (processor is null || !processor.IsInitialized)
                return;

            
            LogManager.LogInformation("TDPControlData;{0:0.000};{1:0.000};{2:0.000};", 
                                      HWiNFOManager.process_value_frametime_ms, 
                                      HWiNFOManager.process_value_fps,
                                      HWiNFOManager.process_value_tdp_actual);
            
            
            // @@@ Todo, improve delta time since previous measurement!

        }

        private void AutoTDP_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (processor is null || !processor.IsInitialized)
                return;

            if (HWiNFOManager.process_value_tdp_actual == 0.0 || HWiNFOManager.process_value_fps == 0.0)
            {
                return;
            }

            if (Monitor.TryEnter(AutoTDPLock))
            {

                // @@@Todo long term
                // - run this when sensor values actually come in from hwinfo (less updates requires) and or hwinfo manager class (remove possible 100 msec delay)
                // - Set TDP update to ryzen adjust to 1000 again, if possible, even lower.

                // Todos
                // - When first activated, use tdp actual (filtered 300 msec delay, minium amount of age) and fps actual (filtered 300 msec delay, current)
                // - After the first time we can use TDP setpoint (-1300 msec, relevant for current FPS) or also use actual TDP TBD
                // - Determine ControllerOutputBias when: performance curve error is > 25 % for 500 msec or when FPS setpoint changes
                // - Performance curve error cannot be determined if we update/calculate everytime from the same curve
                // when this is done, give bias updater a timeout of TimeUntillNextTDPSet + FPSSettlingTime, so 100 to 1000 msec + 1300 msec.When doing this, prevent Proportional kick and Integrator wind up by disabling it for one round.
                // -Scene estimator needs to use TDP value that is at least ~1300 msec old as that reflects the time delay between set TDP and FPS.Use shifting array(https://stackoverflow.com/a/2381353) 

                FPSActualFilteredValue = FPSActualFiltered.axis1Filter.Filter(HWiNFOManager.process_value_fps, 0.1);
                TDPActualFilteredValue = FPSActualFiltered.axis1Filter.Filter(HWiNFOManager.process_value_tdp_actual, 0.1);
                double FPSActual = FPSActualFilteredValue; //HWiNFOManager.process_value_fps;

                // Update Controller Output Bias only on:
                // - First run and enough time has passed to get an idea for actual FPS based on earlier TDP actual
                // - FPS Setpoint change and TDP Setpoint has been done earlier and has taken effect
                // - Scene change @@@ TBD, probably use performance curve multiplier error, need to play games and check actual scene change data

                Double FPSSetpoint = SettingsManager.GetDouble("QuickToolsPerformanceAutoTDPFPSValue");
                if (FPSSetpointPrevious == Double.NaN) { FPSSetpointPrevious = FPSSetpoint; }

                // TDP Setpoint valid rolling buffer
                // As actual FPS has a ~1300 msec delay, in some cases it's better to use the TDP setpoint from 1300 msec earlier
                // Use rolling array buffer for that.
                Array.Copy(TDPSetpointHistory, 0, TDPSetpointHistory, 1, TDPSetpointHistory.Length - 1);
                // Put current TDP setpoint into array
                TDPSetpointHistory[0] = TDPSetpoint;
                // Valid TDP setpoint for current FPS is n time older TDP
                // @@@ Todo, need to put this in a generic formula taking into account dead times, loop time, settling time etc
                TDPSetpointValid = TDPSetpointHistory[12] + (TDPSetpointHistory[2] - TDPSetpointHistory[12]) * 0.68;

                // Detect scene change
                // Scene change percentage for certain duration
                int PerformanceCurveErrorDuration = 0;
                if (Math.Abs(PerformanceCurveError - 1) * 100 >= 25 && PerformanceCurveErrorDuration > 3000) 
                {

                }
                // User request FPS setpoint, use TDP setpoint that was set earlier and for which current FPS should be valid
                else if (FPSSetpoint != FPSSetpointPrevious)
                {
                    AutoTDPState = AUTO_TDP_STATE_CO_BIAS;
                    //LogManager.LogInformation("AutoTDP USer FPS Request;{0:0.0}", FPSSetpoint);

                    // Change has been caught, update for next cycles
                    FPSSetpointPrevious = FPSSetpoint;
                }

                // Statemachine
                // - Idle / off
                // - Performance Curver Tester
                // - Determine Controller Output Bias
                // - PID control
                // Intentionally with seperate if statements so we can perform the next state in the same program cycle
                if (AutoTDPState == AUTO_TDP_STATE_IDLE)
                {
                    AutoTDPState = AUTO_TDP_STATE_CO_BIAS;
                    
                }

                if (AutoTDPState == AUTO_TDP_STATE_CO_BIAS) 
                {
                    PTermEnabled = 0;
                    ITermEnabled = 0;
                    DTermEnabled = 0;

                    // Prevent underflow
                    if (COBiasAttemptTimeoutMilliSec <= 0){ COBiasAttemptTimeoutMilliSec = 0; }

                    // Once COBias has been set, wait untill FPS responce 
                    if (COBiasAttemptTimeoutMilliSec == 0)
                    {                   

                        StartTDPWatchdog();
                        stopwatch.Start();

                    }

                    COBiasAttemptTimeoutMilliSec -= INTERVAL_AUTO_TDP;
                }

                if (AutoTDPState == AUTO_TDP_STATE_PID_CONTROL) { 
                }

                //  LogManager.LogInformation("AutoTDPData;{0:0.000};{1:0.000};{2:0.000};", HWiNFOManager.process_value_fps, HWiNFOManager.process_value_tdp_actual, COBias);

                // user requested to halt auto TDP, stop watchdogs
                if (AutoTDPWatchdogPendingStop)
                {
                    AutoTDPWatchdog.Stop();
                    stopwatch.Start();
                    cpuWatchdogPendingStop = true;
                    AutoTDPWatchdogPendingStop = false;
                    AutoTDPState = AUTO_TDP_STATE_IDLE;
                }

                Monitor.Exit(AutoTDPLock);
            }

        }

        private double DetermineControllerOutputBias(double WantedFPS, double ActualFPS, double TDPEarlierSetOrActual)
        {
            // @@@ todo, adjust performance curve differently then a global?

            double ControllerOutputBias = 0.0;
            double ExpectedFPS = 0.0;
            TDPEarlierSetOrActual = Math.Clamp(TDPEarlierSetOrActual, MinTDP, MaxTDP); // prevent out of bounds noise
            ActualFPS = Math.Clamp(ActualFPS, 20, 90); // Prevent using exterme FPS values.
            int i;

            // @@@ Todo, determine node amount
            int NodeAmount = 21;


            // Convert xy list to separate single lists
            double[] X = new double[NodeAmount];
            double[] Y = new double[NodeAmount];

            for (int idx = 0; idx < NodeAmount; idx++)
            {
                X[idx] = PerformanceCurve[idx, 0];
                Y[idx] = PerformanceCurve[idx, 1];
            }

            // Check performance curve for current TDP and corresponding expected FPS
            // Use actual FPS for "earlier" TDP setpoint

            // Figure out between which two nodes the current TDP setpoint or actual is
            i = Array.FindIndex(X, k => TDPEarlierSetOrActual <= k);

            if (i < 0 || i > NodeAmount)
            {
                LogManager.LogInformation("Array.FindIndex out of bounds for TDP Setpoint or actual of: {0:000}", TDPEarlierSetOrActual);
                return Math.Clamp(ControllerOutputBias, MinTDP, MaxTDP);
            }

            // Interpolate between those two points
            TDPEarlierSetOrActual = Math.Clamp(TDPEarlierSetOrActual, MinTDP, MaxTDP);
            ExpectedFPS = Y[i - 1] + (TDPEarlierSetOrActual - X[i - 1]) * (Y[i] - Y[i - 1]) / (X[i] - X[i - 1]);

            //LogManager.LogInformation("For TDPSetpoint {0:0.000} we have ExpectedFPS {1:0.000} ", TDPSetpoint, ExpectedFPS);

            // Determine ratio difference between expected FPS and actual
            FPSRatio = (ActualFPS / ExpectedFPS);

            //LogManager.LogInformation("FPSRatio {0:0.000} = ExpectedFPS {1:0.000} / ActualFPS {2:0.000}", FPSRatio, ExpectedFPS, HWiNFOManager.process_value_fps);

            // Update whole performance curve FPS values
            for (int idx = 0; idx < NodeAmount; idx++)
            {

                double ScalingDamper = 0.96; // 0.95 too slow 0.96 seems ok

                // @@@ Todo, rework back to simpler variant again? Scaling damper always seems negative thus far

                if (FPSRatio >= 1)
                {
                    if (ScalingDamper < 1.0) { FPSRatio = ((FPSRatio - 1) * ScalingDamper) + 1; }
                    if (ScalingDamper >= 1.0) { FPSRatio = ((FPSRatio - 1) / ScalingDamper) + 1; }
                }
                else
                {
                    if (ScalingDamper < 1.0) { FPSRatio = 1 - ((1 - FPSRatio) * ScalingDamper); }
                    if (ScalingDamper >= 1.0) { FPSRatio = 1 - ((1 - FPSRatio) / ScalingDamper); }
                }

                PerformanceCurve[idx, 1] = PerformanceCurve[idx, 1] * FPSRatio;
                Y[idx] = PerformanceCurve[idx, 1];
            }

            //LogManager.LogInformation("Updated curve:");
            //LogManager.LogInformation("X {0} ", X);
            //LogManager.LogInformation("Y {0} ", Y);

            // Check performance curve for new TDP required for requested FPS
            // cautious of limits, 
            //if highest FPS in performance curve is lower then requested FPS, set TDP max
            if (Y[NodeAmount - 1] < WantedFPS)
            {
                ControllerOutputBias = MaxTDP;
            }
            //if lowest FPS in performance curve is higher then requested FPS, set TDP min
            else if (Y[0] > WantedFPS)
            {
                ControllerOutputBias = MinTDP;
            }
            else
            {
                // Figure out between which two nodes the wanted FPS is
                i = Array.FindIndex(Y, k => WantedFPS <= k);

                // Interpolate between those two points
                ControllerOutputBias = X[i - 1] + (WantedFPS - Y[i - 1]) * (X[i] - X[i - 1]) / (Y[i] - Y[i - 1]);
                //LogManager.LogInformation("For WantedFPS {0:0.0} we have interpolated TDPSetpoint {1:0.000} ", WantedFPS, TDPSetpoint);

            }

            return Math.Clamp(ControllerOutputBias, MinTDP, MaxTDP);
        }

        private double[,] DeterminePerformanceCurveControllerProcessGains(double[,] PerformanceCurve)
        {
            // Process gain
            // https://controlguru.com/process-gain-is-the-how-far-variable/
            // Process gain = steady state change in measured process variable delta / steady state change in controller output delta

            // Example
            // FPS Delta 49.2 - 40.5 = 8.7
            // TDP Delta 20.165 - 12.23 = 7.935
            // Process gain = 1,0964 = 8.7 / 7.935

            // Note, game dependent, scene dependent (probably)

            double TDPDelta;
            double FPSDelta;
            double ProcessGain;

            // @@@ Todo, could possible combine some stuff into functions
            // Determine process gain for each point in curve
            int NodeAmount = 21;
            for (int idx = 0; idx < NodeAmount; idx++)
            {
                // In case of in between nodes, determine avarage
                if (idx != 0 && idx != NodeAmount - 1)
                {
                    // From previous node
                    TDPDelta = PerformanceCurve[idx, 0] - PerformanceCurve[idx - 1, 0];
                    FPSDelta = PerformanceCurve[idx, 1] - PerformanceCurve[idx - 1, 1];

                    // @@@ Todo, process gain should always be postive, but not all performance curves are perfect...
                    ProcessGain = Math.Abs(FPSDelta / TDPDelta);

                    // To next node
                    TDPDelta = PerformanceCurve[idx + 1, 0] - PerformanceCurve[idx, 0];
                    FPSDelta = PerformanceCurve[idx + 1, 1] - PerformanceCurve[idx, 1];

                    double ProcessGain2;
                    // @@@ Todo, process gain should always be postive, but not all performance curves are perfect...
                    ProcessGain2 = Math.Abs(FPSDelta / TDPDelta);

                    // Average
                    PerformanceCurve[idx, 2] = (ProcessGain + ProcessGain2) / 2;

                }
                // In case of first node, use next point only
                else if (idx == 0) 
                {
                    TDPDelta = PerformanceCurve[idx + 1, 0] - PerformanceCurve[idx, 0];
                    FPSDelta = PerformanceCurve[idx + 1, 1] - PerformanceCurve[idx, 1];

                    // @@@ Todo, process gain should always be postive, but not all performance curves are perfect...
                    ProcessGain = Math.Abs(FPSDelta / TDPDelta);

                    PerformanceCurve[idx, 2] = ProcessGain;
                }
                // In case of last node, use previous point only
                else if (idx == NodeAmount - 1)
                {
                    TDPDelta = PerformanceCurve[idx, 0] - PerformanceCurve[idx - 1, 0];
                    FPSDelta = PerformanceCurve[idx, 1] - PerformanceCurve[idx - 1, 1];

                    // @@@ Todo, process gain should always be postive, but not all performance curves are perfect...
                    ProcessGain = Math.Abs(FPSDelta / TDPDelta);

                    PerformanceCurve[idx, 2] = ProcessGain;
                }
            }

            // Debug output, performance curve
            if (false)
            {
                // Header
                LogManager.LogInformation(",TDP,FPS,ProcessGain");

                // Content
                for (int idx = 0; idx < NodeAmount; idx++)
                {
                    LogManager.LogInformation(",{0:0.000},{1:0.000},{2:0.000}", PerformanceCurve[idx, 0], PerformanceCurve[idx, 1], PerformanceCurve[idx, 2]);
                }
            }

            return PerformanceCurve;

        }

        private double DetermineControllerProcessGain(double[,] PerformanceCurve, double FPSCurrent)
        {
            double ProcessGainKp;

            // Linear interpolation
            // @@@ Todo, we seem to be doing this linear interpolation quite often in the same way... uhhh

            // Convert FPS and ProcessGains to separate single lists
            int NodeAmount = 21;

            double[] X = new double[NodeAmount];
            double[] Y = new double[NodeAmount];

            for (int idx = 0; idx < NodeAmount; idx++)
            {
                X[idx] = PerformanceCurve[idx, 1]; // FPS on X axis
                Y[idx] = PerformanceCurve[idx, 2]; // Process gain on Y axis
            }

            // If current FPS is higher then highest performance curve FPS value, return max process gain
            if (X[NodeAmount - 1] < FPSCurrent)
            {
                ProcessGainKp = Y[NodeAmount - 1];
            }
            // If current FPS is lower then lowest performance curve FPS value, return lowest process gain
            else if (X[0] > FPSCurrent)
            {
                ProcessGainKp = Y[0];
            }
            else
            {
                int i;

                // Figure out between which two nodes the current FPS is
                i = Array.FindIndex(X, k => FPSCurrent <= k);

                // Interpolate between those two points
                ProcessGainKp = Y[i - 1] + (FPSCurrent - X[i - 1]) * (Y[i] - Y[i - 1]) / (X[i] - X[i - 1]);
            }

            // @@@ Todo, process gain should always be postive, but not all performance curves are perfect...
            return Math.Abs(ProcessGainKp);
        }

        internal void StartGPUWatchdog()
        {
            gfxWatchdog.Start();
        }

        internal void StopGPUWatchdog()
        {
            gfxWatchdogPendingStop = true;
        }

        internal void StopTDPWatchdog()
        {
            cpuWatchdogPendingStop = true;
        }

        internal void StartTDPWatchdog()
        {
            cpuWatchdog.Start();
        }
        internal void StartAutoTDPWatchdog()
        {
            AutoTDPWatchdog.Start();
        }
        internal void StopAutoTDPWatchdog()
        {
            AutoTDPWatchdogPendingStop = true;
        }

        public void RequestTDP(PowerType type, double value, bool UserRequested = true)
        {
            int idx = (int)type;

            if (UserRequested)
                FallbackTDP[idx] = value;

            // update value read by timer
            StoredTDP[idx] = value;
        }

        public void RequestTDP(double[] values, bool UserRequested = true)
        {
            if (UserRequested)
                FallbackTDP = values;

            // update value read by timer
            StoredTDP = values;
        }

        public void RequestGPUClock(double value, bool UserRequested = true)
        {
            if (UserRequested)
                FallbackGfxClock = value;

            // update value read by timer
            StoredGfxClock = value;
        }

        public void RequestPowerMode(int idx)
        {
            RequestedPowerMode = PowerMode.PowerModes[idx];
            LogManager.LogInformation("User requested power scheme: {0}", RequestedPowerMode);

            PowerSetActiveOverlayScheme(RequestedPowerMode);
        }

        #region events
        private void Processor_StatusChanged(bool CanChangeTDP, bool CanChangeGPU)
        {
            ProcessorStatusChanged?.Invoke(CanChangeTDP, CanChangeGPU);
        }

        private void Processor_ValueChanged(PowerType type, float value)
        {
            PowerValueChanged?.Invoke(type, value);
        }

        private void Processor_LimitChanged(PowerType type, int limit)
        {
            int idx = (int)type;
            CurrentTDP[idx] = limit;

            // raise event
            PowerLimitChanged?.Invoke(type, limit);
        }

        private void Processor_MiscChanged(string misc, float value)
        {
            switch (misc)
            {
                case "gfx_clk":
                    {
                        CurrentGfxClock = value;
                    }
                    break;
            }
        }
        #endregion

        public override void Start()
        {
            // initialize watchdog(s)
            powerWatchdog.Start();

            // initialize processor
            processor = Processor.GetCurrent();

            if (!processor.IsInitialized)
                return;

            // higher interval on Intel CPUs to avoid CPU overload
            if (processor.GetType() == typeof(IntelProcessor))
            {
                cpuWatchdog.Interval = INTERVAL_INTEL;

                int VulnerableDriverBlocklistEnable = Convert.ToInt32(RegistryUtils.GetHKLM(@"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable"));
                if (VulnerableDriverBlocklistEnable == 1)
                {
                    cpuWatchdog.Stop();
                    processor.Stop();

                    LogManager.LogWarning("Core isolation, Memory integrity setting is turned on. TDP read/write is disabled");
                }
            }

            processor.ValueChanged += Processor_ValueChanged;
            processor.StatusChanged += Processor_StatusChanged;
            processor.LimitChanged += Processor_LimitChanged;
            processor.MiscChanged += Processor_MiscChanged;
            processor.Initialize();

            base.Start();
        }

        public override void Stop()
        {
            if (!IsInitialized)
                return;

            processor.Stop();

            powerWatchdog.Stop();
            cpuWatchdog.Stop();
            gfxWatchdog.Stop();
            AutoTDPWatchdog.Stop();

            base.Stop();
        }
    }
}
