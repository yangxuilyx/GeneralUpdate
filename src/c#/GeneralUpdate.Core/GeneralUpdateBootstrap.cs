﻿using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GeneralUpdate.Common;
using GeneralUpdate.Common.Download;
using GeneralUpdate.Common.Internal;
using GeneralUpdate.Common.Internal.Bootstrap;
using GeneralUpdate.Common.Internal.Event;
using GeneralUpdate.Common.Internal.Strategy;
using GeneralUpdate.Common.Shared.Object;
using GeneralUpdate.Core.Strategys;

namespace GeneralUpdate.Core
{
    public class GeneralUpdateBootstrap : AbstractBootstrap<GeneralUpdateBootstrap, IStrategy>
    {
        private readonly GlobalConfigInfo _configInfo;
        private IStrategy? _strategy;

        public GeneralUpdateBootstrap()
        {
            try
            {
                //Gets values from system environment variables (ClientParameter object to base64 string).
                var json = Environment.GetEnvironmentVariable("ProcessInfo", EnvironmentVariableTarget.User);
                if (string.IsNullOrWhiteSpace(json))
                    throw new ArgumentException("ProcessInfo object cannot be null!");
                
                var processInfo = JsonSerializer.Deserialize<ProcessInfo>(json);
                if (processInfo == null)
                    throw new ArgumentException("ProcessInfo object cannot be null!");
                
                _configInfo = new()
                {
                    MainAppName = processInfo.AppName,
                    InstallPath = processInfo.InstallPath,
                    ClientVersion = processInfo.CurrentVersion,
                    LastVersion = processInfo.LastVersion,
                    UpdateLogUrl = processInfo.UpdateLogUrl,
                    Encoding = ToEncoding(processInfo.CompressEncoding),
                    Format = processInfo.CompressFormat,
                    DownloadTimeOut = processInfo.DownloadTimeOut,
                    AppSecretKey = processInfo.AppSecretKey,
                    UpdateVersions = processInfo.UpdateVersions,
                    TempPath = $"{GeneralFileManager.GetTempDirectory(processInfo.LastVersion)}{Path.DirectorySeparatorChar}"
                };
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Client parameter json conversion failed, please check whether the parameter content is legal : {ex.Message},{ex.StackTrace}.");
            }
        }

        public override async Task<GeneralUpdateBootstrap> LaunchAsync()
        {
            var manager = new DownloadManager(_configInfo.TempPath, _configInfo.Format, _configInfo.DownloadTimeOut);
            manager.MultiAllDownloadCompleted += OnMultiAllDownloadCompleted;
            manager.MultiDownloadCompleted += OnMultiDownloadCompleted;
            manager.MultiDownloadError += OnMultiDownloadError;
            manager.MultiDownloadProgressChanged += OnMultiDownloadProgressChanged;
            manager.MultiDownloadStatistics += OnMultiDownloadStatistics;
            foreach (var versionInfo in _configInfo.UpdateVersions)
            {
                manager.Add(new DownloadTask(manager, versionInfo));
            }
            await manager.LaunchTasksAsync();
            return this;
        }

        #region public method

        public GeneralUpdateBootstrap AddListenerMultiAllDownloadCompleted(
            Action<object, MultiAllDownloadCompletedEventArgs> callbackAction)
        => AddListener(callbackAction);

        public GeneralUpdateBootstrap AddListenerMultiDownloadProgress(
            Action<object, MultiDownloadProgressChangedEventArgs> callbackAction)
        => AddListener(callbackAction);

        public GeneralUpdateBootstrap AddListenerMultiDownloadCompleted(
            Action<object, MultiDownloadCompletedEventArgs> callbackAction)
        => AddListener(callbackAction);

        public GeneralUpdateBootstrap AddListenerMultiDownloadError(
            Action<object, MultiDownloadErrorEventArgs> callbackAction)
        => AddListener(callbackAction);

        public GeneralUpdateBootstrap AddListenerMultiDownloadStatistics(
            Action<object, MultiDownloadStatisticsEventArgs> callbackAction)
        => AddListener(callbackAction);

        public GeneralUpdateBootstrap AddListenerException(Action<object, ExceptionEventArgs> callbackAction)
        => AddListener(callbackAction);

        #endregion

        protected override void ExecuteStrategy()
        {
            _strategy?.Create(_configInfo);
            _strategy?.Execute();
        }

        protected override GeneralUpdateBootstrap StrategyFactory()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                _strategy = new WindowsStrategy();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                _strategy = new LinuxStrategy();
            else
                throw new PlatformNotSupportedException("The current operating system is not supported!");

            return this;
        }
        
        private GeneralUpdateBootstrap AddListener<TArgs>(Action<object, TArgs> callbackAction) where TArgs : EventArgs
        {
            Debug.Assert(callbackAction!= null);
            EventManager.Instance.AddListener(callbackAction);
            return this;
        }

        private void OnMultiDownloadStatistics(object sender, MultiDownloadStatisticsEventArgs e)
        => EventManager.Instance.Dispatch(sender, e);

        private void OnMultiDownloadProgressChanged(object sender, MultiDownloadProgressChangedEventArgs e)
        => EventManager.Instance.Dispatch(sender, e);

        private void OnMultiDownloadCompleted(object sender, MultiDownloadCompletedEventArgs e)
        => EventManager.Instance.Dispatch(sender, e);

        private void OnMultiDownloadError(object sender, MultiDownloadErrorEventArgs e)
        => EventManager.Instance.Dispatch(sender, e);

        private void OnMultiAllDownloadCompleted(object sender, MultiAllDownloadCompletedEventArgs e)
        {
            EventManager.Instance.Dispatch(sender, e);
            ExecuteStrategy();
        }
        
        private static Encoding ToEncoding(int encodingType) => encodingType switch
        {
            1 => Encoding.UTF8,
            2 => Encoding.UTF7,
            3 => Encoding.UTF32,
            4 => Encoding.Unicode,
            5 => Encoding.BigEndianUnicode,
            6 => Encoding.ASCII,
            7 => Encoding.Default,
            _ => throw new ArgumentException("Encoding type is not supported!")
        };
    }
}