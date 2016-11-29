﻿using StagWare.FanControl.Configurations;
using StagWare.FanControl.Plugins;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace StagWare.FanControl
{
    public class FanControl : IDisposable
    {
        #region Constants

#if DEBUG

        private const int MinPollInterval = 0;

#else

        private const int MinPollInterval = 100;

#endif

        public const int EcTimeout = 200;
        public const int MaxLockTimeout = 500;
        public const int DefaultPollInterval = 3000;
        public const string PluginsFolderDefaultName = "Plugins";
        public const int AutoFanSpeedPercentage = Fan.AutoFanSpeed;

        #endregion

        #region Private Fields

        private readonly object syncRoot = new object();

        private Timer timer;
        private AsyncOperation asyncOp;

        private readonly int pollInterval;
        private readonly int lockTimeout;
        private readonly FanControlConfigV2 config;

        private readonly ITemperatureFilter tempFilter;
        private readonly ITemperatureMonitor tempMon;
        private readonly IEmbeddedController ec;
        private readonly Fan[] fans;

        private volatile bool readOnly;
        private volatile float temperature;
        private volatile float[] requestedSpeeds;
        private volatile FanInformation[] fanInfo;

        #endregion

        #region Constructor

        public FanControl(FanControlConfigV2 config)
            : this(config, PluginsDirectory)
        {
        }

        public FanControl(FanControlConfigV2 config, string pluginsPath) :
            this(
            config,
            new ArithmeticMeanTemperatureFilter(Math.Max(config.EcPollInterval, MinPollInterval)),
            pluginsPath)
        { }

        public FanControl(FanControlConfigV2 config, ITemperatureFilter tempFilter, string pluginsPath)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            if (tempFilter == null)
            {
                throw new ArgumentNullException("filter");
            }

            if (pluginsPath == null)
            {
                throw new ArgumentNullException("pluginsPath");
            }

            if (!Directory.Exists(pluginsPath))
            {
                throw new DirectoryNotFoundException(pluginsPath + " could not be found.");
            }

            var ecLoader = new FanControlPluginLoader<IEmbeddedController>(pluginsPath);
            this.ec = ecLoader.FanControlPlugin;
            this.EmbeddedControllerPluginId = ecLoader.FanControlPluginId;

            if (this.ec == null)
            {
                throw new PlatformNotSupportedException("Could not load a compatible EC plugin.");
            }

            var tempMonloader = new FanControlPluginLoader<ITemperatureMonitor>(pluginsPath);
            this.tempMon = tempMonloader.FanControlPlugin;
            this.TemperatureMonitorPluginId = tempMonloader.FanControlPluginId;

            if (this.tempMon == null)
            {
                throw new PlatformNotSupportedException("Could not load a  compatible temperature monitoring plugin");
            }
            
            this.tempFilter = tempFilter;
            this.config = (FanControlConfigV2)config.Clone();
            this.pollInterval = config.EcPollInterval;
            this.lockTimeout = Math.Min(MaxLockTimeout, config.EcPollInterval);
            this.requestedSpeeds = new float[config.FanConfigurations.Count];
            this.fanInfo = new FanInformation[config.FanConfigurations.Count];
            this.fans = new Fan[config.FanConfigurations.Count];
            this.asyncOp = AsyncOperationManager.CreateOperation(null);

            for (int i = 0; i < config.FanConfigurations.Count; i++)
            {
                var cfg = config.FanConfigurations[i];
                string fanName = cfg.FanDisplayName;

                if (string.IsNullOrWhiteSpace(fanName))
                {
                    fanName = "Fan #" + (i + 1);
                }

                this.fanInfo[i] = new FanInformation(0, 0, true, false, cfg.FanDisplayName);
                this.fans[i] = new Fan(this.ec, cfg, config.CriticalTemperature, config.ReadWriteWords);
                this.requestedSpeeds[i] = AutoFanSpeedPercentage;
            }
        }

        #endregion

        #region Events

        public event EventHandler EcUpdated;

        #endregion

        #region Properties

        public static string PluginsDirectory
        {
            get
            {
                return Path.Combine(AssemblyDirectory, PluginsFolderDefaultName);
            }
        }

        public static string AssemblyDirectory
        {
            get
            {
                string codeBase = Assembly.GetExecutingAssembly().CodeBase;
                UriBuilder uri = new UriBuilder(codeBase);
                string path = Uri.UnescapeDataString(uri.Path);
                return Path.GetDirectoryName(path);
            }
        }

        public string TemperatureMonitorPluginId { get; private set; }
        public string EmbeddedControllerPluginId { get; private set; }

        public float Temperature
        {
            get { return this.temperature; }
        }

        public bool Enabled
        {
            get { return this.timer != null; }
        }

        public bool ReadOnly
        {
            get { return this.readOnly; }
        }

        public string TemperatureSourceDisplayName
        {
            get
            {
                if (this.tempMon == null || !this.tempMon.IsInitialized)
                {
                    return null;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(this.tempMon.TemperatureSourceDisplayName))
                    {
                        return this.TemperatureMonitorPluginId;
                    }
                    else
                    {
                        return this.tempMon.TemperatureSourceDisplayName;
                    }
                }
            }
        }

        public ReadOnlyCollection<FanInformation> FanInformation
        {
            get
            {
                return Array.AsReadOnly(fanInfo);
            }
        }

        #endregion

        #region Public Methods

        public void Start(bool readOnly = true)
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(nameof(FanControl));
            }

            if (this.Enabled)
            {
                if (this.readOnly != readOnly)
                {
                    if (readOnly)
                    {
                        ResetEc();
                    }
                    else
                    {
                        InitializeRegisterWriteConfigurations();
                    }

                    this.readOnly = readOnly;
                }
            }
            else
            {
                if (!this.tempMon.IsInitialized)
                {
                    this.tempMon.Initialize();
                }

                if (!this.ec.IsInitialized)
                {
                    this.ec.Initialize();
                }

                if (!readOnly)
                {
                    InitializeRegisterWriteConfigurations();
                }

                this.readOnly = readOnly;

                if (this.timer == null)
                {
                    this.timer = new Timer(new TimerCallback(TimerCallback), null, 0, this.pollInterval);
                }
            }
        }

        public void SetTargetFanSpeed(float speed, int fanIndex)
        {
            if (fanIndex >= 0 && fanIndex < this.requestedSpeeds.Length)
            {
                Thread.VolatileWrite(ref this.requestedSpeeds[fanIndex], speed);

                if (this.Enabled)
                {
                    ThreadPool.QueueUserWorkItem(TimerCallback, null);
                }
            }
            else
            {
                throw new IndexOutOfRangeException("fanIndex");
            }
        }

        public void Stop()
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(nameof(FanControl));
            }
            else
            {
                StopFanControlCore();
            }
        }

        #endregion

        #region Protected Methods

        protected void OnEcUpdated()
        {
            if (EcUpdated != null)
            {
                EcUpdated(this, new EventArgs());
            }
        }

        #endregion

        #region Private Methods

        #region Update EC

        private void TimerCallback(object state)
        {
            if (Monitor.TryEnter(syncRoot, lockTimeout))
            {
                try
                {
                    // We don't know which locks the plugins try to acquire internally,
                    // therefore never try to access tempMon after calling ec.AcquireLock()
                    double temp = this.tempMon.GetTemperature();
                    this.temperature = (float)this.tempFilter.FilterTemperature(temp);

                    if (this.ec.AcquireLock(EcTimeout))
                    {
                        try
                        {
                            UpdateEc(this.temperature);
                        }
                        finally
                        {
                            this.ec.ReleaseLock();
                        }

                        asyncOp.Post(args => OnEcUpdated(), null);
                    }
                }
                finally
                {
                    Monitor.Exit(syncRoot);
                }
            }
        }

        private void UpdateEc(float temperature)
        {
            // Re-init if current fan speeds are off by more than 15%
            bool reInitRequired = false;
            var speeds = new float[this.fans.Length];

            for (int i = 0; i < speeds.Length; i++)
            {
                speeds[i] = this.fans[i].GetCurrentSpeed();

                if (Math.Abs(speeds[i] - this.fans[i].TargetSpeed) > 15)
                {
                    reInitRequired = true;
                }
            }

            if (!readOnly)
            {
                ApplyRegisterWriteConfigurations(reInitRequired);
            }

            // Set requested fan speeds
            for (int i = 0; i < this.fans.Length; i++)
            {
                float speed = Thread.VolatileRead(ref this.requestedSpeeds[i]);
                this.fans[i].SetTargetSpeed(speed, temperature, readOnly);
            }

            // Update fanInfo
            this.fanInfo = GetFanInformation();
        }

        private FanInformation[] GetFanInformation()
        {
            var info = new FanInformation[this.fans.Length];

            for (int i = 0; i < this.fans.Length; i++)
            {
                this.fans[i].GetCurrentSpeed();

                info[i] = new FanInformation(
                    this.fans[i].TargetSpeed,
                    this.fans[i].CurrentSpeed,
                    this.fans[i].AutoControlEnabled,
                    this.fans[i].CriticalModeEnabled,
                    this.config.FanConfigurations[i].FanDisplayName);
            }

            return info;
        }

        private void StopFanControlCore()
        {
            if (this.Enabled)
            {
                if (timer != null)
                {
                    // Wait until all callbacks have completed and then dispose the timer
                    using (var handle = new EventWaitHandle(false, EventResetMode.ManualReset))
                    {
                        timer.Dispose(handle);

                        if (handle.WaitOne())
                        {
                            timer = null;
                        }
                    }
                }

                if (!readOnly)
                {
                    ResetEc(true);
                }
            }
        }        

        private void InitializeRegisterWriteConfigurations()
        {
            if (Monitor.TryEnter(syncRoot, lockTimeout))
            {
                try
                {
                    if (!this.ec.AcquireLock(EcTimeout))
                    {
                        throw new TimeoutException("EC initialization failed: Could not acquire EC lock");
                    }

                    try
                    {
                        ApplyRegisterWriteConfigurations(true);
                    }
                    finally
                    {
                        this.ec.ReleaseLock();
                    }
                }
                finally
                {
                    Monitor.Exit(syncRoot);
                }
            }
            else
            {
                throw new TimeoutException("EC initialization failed: Could not enter monitor");
            }
        }

        private void ApplyRegisterWriteConfigurations(bool initializing = false)
        {
            if (this.config.RegisterWriteConfigurations != null)
            {
                foreach (RegisterWriteConfiguration cfg in this.config.RegisterWriteConfigurations)
                {
                    if (initializing || cfg.WriteOccasion == RegisterWriteOccasion.OnWriteFanSpeed)
                    {
                        ApplyRegisterWriteConfig((byte)cfg.Value, (byte)cfg.Register, cfg.WriteMode);
                    }
                }
            }
        }

        private void ApplyRegisterWriteConfig(byte value, byte register, RegisterWriteMode mode)
        {
            if (mode == RegisterWriteMode.And)
            {
                value &= this.ec.ReadByte(register);
            }
            else if (mode == RegisterWriteMode.Or)
            {
                value |= this.ec.ReadByte(register);
            }

            this.ec.WriteByte(register, value);
        }

        #endregion

        #region Reset EC

        private void ResetEc(bool force = false)
        {
            if (!this.config.RegisterWriteConfigurations.Any(x => x.ResetRequired)
                && !this.config.FanConfigurations.Any(x => x.ResetRequired))
            {
                return;
            }

            bool fanControlLockAcquired = Monitor.TryEnter(syncRoot, MaxLockTimeout * 2);

            if (!fanControlLockAcquired && !force)
            {
                throw new TimeoutException("EC reset failed: Could not enter monitor");
            }

            try
            {
                bool ecLockAcquired = this.ec.AcquireLock(EcTimeout * 2);

                if (!ecLockAcquired && !force)
                {
                    throw new TimeoutException("EC reset failed: Could not acquire EC lock");
                }

                // If force is true, try to reset the EC even if AquireLock failed
                try
                {
                    int tries = force ? 3 : 1;

                    for (int i = 0; i < tries; i++)
                    {
                        ResetRegisterWriteConfigs();
                        ResetFans();
                    }
                }
                finally
                {
                    if (ecLockAcquired)
                    {
                        this.ec.ReleaseLock();
                    }
                }
            }
            finally
            {
                if (fanControlLockAcquired)
                {
                    Monitor.Exit(syncRoot);
                }
            }
        }

        private void ResetFans()
        {
            foreach (Fan fan in this.fans)
            {
                fan.Reset();
            }
        }

        private void ResetRegisterWriteConfigs()
        {
            foreach (RegisterWriteConfiguration cfg in this.config.RegisterWriteConfigurations)
            {
                if (cfg.ResetRequired)
                {
                    ApplyRegisterWriteConfig((byte)cfg.ResetValue, (byte)cfg.Register, cfg.ResetWriteMode);
                }
            }
        }

        #endregion

        #endregion

        #region IDisposable implementation

        private bool disposed = false;

        public void Dispose()
        {
            if (!this.disposed)
            {
                this.disposed = true;
                StopFanControlCore();

                if (this.asyncOp != null)
                {
                    this.asyncOp.OperationCompleted();
                }

                if (this.ec != null)
                {
                    this.ec.Dispose();
                }

                if (this.tempMon != null)
                {
                    this.tempMon.Dispose();
                }

                GC.SuppressFinalize(this);
            }
        }

        #endregion
    }
}
