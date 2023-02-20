using ControllerCommon;
using ControllerCommon.Managers;
using ControllerCommon.Processor;
using ControllerCommon.Utils;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Timers;
using Windows.ApplicationModel.Store;
using Timer = System.Timers.Timer;

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

        private const short INTERVAL_DEFAULT = 2000;            // default interval between value scans
        private const short INTERVAL_INTEL = 5000;              // intel interval between value scans
        private const short INTERVAL_DEGRADED = 2000;          // degraded interval between value scans
        private const short INTERVAL_SENSOR = 100;

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
        private double[,] PerformanceCurve = new double[,] {  { 5, 15 },
                                                                  { 6, 18 }, { 7, 25 }, { 8, 36 }, { 9, 46 }, { 10, 54 },
                                                                  { 11, 59 }, { 12, 64 }, { 13, 68 }, { 14, 71 }, { 15, 74 },
                                                                  { 16, 73}, { 17, 74 }, { 18, 75 }, { 19, 76 }, { 20, 80 },
                                                                  { 21, 81 }, { 22, 81 }, { 23, 81 }, { 24, 82 }, { 25, 84 },
                                                               };

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
                int algo_choice = 2;

                if (algo_choice == 1)
                {
                    

                    if (HWiNFOManager.process_value_tdp_actual != 0.0 && HWiNFOManager.process_value_fps != 0.0 && HWiNFOManager.process_value_fps != 0.0)
                    {
                        TDPSetpoint = TDPSetpoint + (TDPSetpoint / HWiNFOManager.process_value_fps) * (WantedFPS - HWiNFOManager.process_value_fps);
                    }
                }
                else if (algo_choice == 2){


                    int i;
                    double ExpectedFPS = 0.0f;
                    int NodeAmount = 21;
                    double DeltaError = 0.0f;
                    double ProcessValueNew = 0.0f;
                    double DTerm = 0.0f;
                    double DeltaTimeSec = INTERVAL_DEFAULT / 1000; // @todo, replace with better measured timer 
                    double DFactor = 0.07; // 0.09 caused issues at 30 FPS, 0.18 goes even more unstable
                    double DTermEnabled = 1;


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
                    i = Array.FindIndex(X, k => TDPSetpoint <= k);

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

                        double ScalingDamper = 0.96; // 0.95 too slow

                        if (FPSRatio >= 1)
                        {
                            if (ScalingDamper < 1.0) { FPSRatio = ((FPSRatio - 1) * ScalingDamper) + 1; }
                            if (ScalingDamper >= 1.0) { FPSRatio = ((FPSRatio - 1) / ScalingDamper) + 1; }
                        }
                        else {
                            if (ScalingDamper < 1.0) { FPSRatio = 1 - ((1 - FPSRatio) * ScalingDamper); }
                            if (ScalingDamper >= 1.0) { FPSRatio = 1 - ((1 - FPSRatio) / ScalingDamper); }
                        }

                        PerformanceCurve[idx,1] = PerformanceCurve[idx,1] * FPSRatio;
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

                // HWiNFOManager.process_value_tdp_actual or set as ratio?

                // Update all stored TDP values
                StoredTDP[0] = StoredTDP[1] = StoredTDP[2] = Math.Clamp(TDPSetpoint,5,25);

                LogManager.LogInformation("TDPSet;;;;{0:0.0000};{1:0.0};{2:0.000};{3:0.0000};{4:0.0000};{5:0.0000}", StoredTDP[0], WantedFPS, TDPSetpointInterpolator, TDPSetpointDerivative, PerformanceCurveError, FPSRatio);

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

            base.Stop();
        }
    }
}
