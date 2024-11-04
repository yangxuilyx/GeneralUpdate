using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GeneralUpdate.ClientCore.Strategys;
using GeneralUpdate.Common;
using GeneralUpdate.Common.Download;
using GeneralUpdate.Common.Internal;
using GeneralUpdate.Common.Internal.Bootstrap;
using GeneralUpdate.Common.Internal.Event;
using GeneralUpdate.Common.Internal.Strategy;
using GeneralUpdate.Common.Shared.Object;
using GeneralUpdate.Common.Shared.Service;

namespace GeneralUpdate.ClientCore;

/// <summary>
///     This component is used only for client application bootstrapping classes.
/// </summary>
public class GeneralClientBootstrap : AbstractBootstrap<GeneralClientBootstrap, IStrategy>
{
    /// <summary>
    /// All update actions of the core object for automatic upgrades will be related to the packet object.
    /// </summary>
    private GlobalConfigInfo? _configinfo;
    private IStrategy? _strategy;
    private Func<bool>? _customSkipOption;
    private readonly List<Func<bool>> _customOptions = new();

    #region Public Methods

    /// <summary>
    /// Main function for booting the update startup.
    /// </summary>
    /// <returns></returns>
    public override async Task<GeneralClientBootstrap> LaunchAsync()
    {
        ExecuteCustomOptions();
        ClearEnvironmentVariable();
        await InitializeDataAsync();
        
        var manager = new DownloadManager(_configinfo.TempPath, _configinfo.Format, _configinfo.DownloadTimeOut);
        manager.MultiAllDownloadCompleted += OnMultiAllDownloadCompleted;
        manager.MultiDownloadCompleted += OnMultiDownloadCompleted;
        manager.MultiDownloadError += OnMultiDownloadError;
        manager.MultiDownloadProgressChanged += OnMultiDownloadProgressChanged;
        manager.MultiDownloadStatistics += OnMultiDownloadStatistics;
        foreach (var versionInfo in _configinfo.UpdateVersions)
        {
            manager.Add(new DownloadTask(manager, versionInfo));
        }
        await manager.LaunchTasksAsync();
        return this;
    }

    /// <summary>
    ///     Configure server address (Recommended Windows,Linux,Mac).
    /// </summary>
    /// <param name="url">Remote server address.</param>
    /// <param name="appName">The updater name does not need to contain an extension.</param>
    /// <returns></returns>
    /// <exception cref="Exception">Parameter initialization is abnormal.</exception>
    public GeneralClientBootstrap SetConfig(Configinfo configInfo)
    {
        Debug.Assert(configInfo != null, "configInfo should not be null");
        configInfo?.Validate();
        _configinfo = new GlobalConfigInfo
        {
            AppName = configInfo.AppName,
            MainAppName = configInfo.MainAppName,
            ClientVersion = configInfo.ClientVersion,
            InstallPath = configInfo.InstallPath,
            UpdateLogUrl = configInfo.UpdateLogUrl,
            UpdateUrl = configInfo.UpdateUrl,
            ReportUrl = configInfo.ReportUrl,
            AppSecretKey = configInfo.AppSecretKey,
            BlackFormats = configInfo.BlackFormats,
            BlackFiles = configInfo.BlackFiles,
            Platform = configInfo.Platform,
            ProductId = configInfo.ProductId,
            UpgradeClientVersion = configInfo.UpgradeClientVersion
        };
        return this;
    }

    /// <summary>
    ///     Let the user decide whether to update in the state of non-mandatory update.
    /// </summary>
    /// <param name="func">
    ///     Custom function ,Custom actions to let users decide whether to update. true update false do not
    ///     update .
    /// </param>
    /// <returns></returns>
    public GeneralClientBootstrap SetCustomSkipOption(Func<bool> func)
    {
        Debug.Assert(func != null);
        _customSkipOption = func;
        return this;
    }

    /// <summary>
    ///     Add an asynchronous custom operation.
    ///     In theory, any custom operation can be done. It is recommended to register the environment check method to ensure
    ///     that there are normal dependencies and environments after the update is completed.
    /// </summary>
    /// <param name="func"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public GeneralClientBootstrap AddCustomOption(List<Func<bool>> funcs)
    {
        Debug.Assert(funcs != null && funcs.Any());
        _customOptions.AddRange(funcs);
        return this;
    }

    public GeneralClientBootstrap AddListenerMultiAllDownloadCompleted(
        Action<object, MultiAllDownloadCompletedEventArgs> callbackAction)
        => AddListener(callbackAction);

    public GeneralClientBootstrap AddListenerMultiDownloadProgress(
        Action<object, MultiDownloadProgressChangedEventArgs> callbackAction)
        => AddListener(callbackAction);

    public GeneralClientBootstrap AddListenerMultiDownloadCompleted(
        Action<object, MultiDownloadCompletedEventArgs> callbackAction)
        => AddListener(callbackAction);

    public GeneralClientBootstrap AddListenerMultiDownloadError(
        Action<object, MultiDownloadErrorEventArgs> callbackAction)
        => AddListener(callbackAction);

    public GeneralClientBootstrap AddListenerMultiDownloadStatistics(
        Action<object, MultiDownloadStatisticsEventArgs> callbackAction)
        => AddListener(callbackAction);

    public GeneralClientBootstrap AddListenerException(Action<object, ExceptionEventArgs> callbackAction)
        => AddListener(callbackAction);

    #endregion Public Methods

    #region Private Methods

    private async Task InitializeDataAsync()
    {
        //Request the upgrade information needed by the client and upgrade end, and determine if an upgrade is necessary.
        var mainResp = await VersionService.Validate(_configinfo.UpdateUrl
            , _configinfo.ClientVersion
            ,1
            ,_configinfo.AppSecretKey
            ,_configinfo.Platform
            ,_configinfo.ProductId);
        
        var upgradeResp = await VersionService.Validate(_configinfo.UpdateUrl
            , _configinfo.UpgradeClientVersion
            ,2
            ,_configinfo.AppSecretKey
            ,_configinfo.Platform
            ,_configinfo.ProductId);

        _configinfo.IsUpgradeUpdate = upgradeResp.Body.Count > 0;
        _configinfo.IsMainUpdate = mainResp.Body.Count > 0;
        //No need to update, return directly.
        if (!_configinfo.IsMainUpdate && !_configinfo.IsUpgradeUpdate) return;

        //If the main program needs to be forced to update, the skip will not take effect.
        var isForcibly = CheckForcibly(mainResp.Body) || CheckForcibly(upgradeResp.Body);
        if (CanSkip(isForcibly)) return;

        _configinfo.UpdateVersions = upgradeResp.Body.OrderBy(x => x.ReleaseDate).ToList();
        _configinfo.LastVersion = _configinfo.UpdateVersions.Last().Version;
        _configinfo.Encoding = GetOption(UpdateOption.Encoding) ?? Encoding.Default;
        _configinfo.Format = GetOption(UpdateOption.Format)?? "zip";
        _configinfo.DownloadTimeOut = GetOption(UpdateOption.DownloadTimeOut) == 0 ? 60 : GetOption(UpdateOption.DownloadTimeOut);
        _configinfo.DriveEnabled = GetOption(UpdateOption.Drive);
        _configinfo.TempPath = GeneralFileManager.GetTempDirectory(_configinfo.LastVersion);
        
        //Initialize the process transfer parameter object.
        var processInfo = new ProcessInfo(_configinfo.MainAppName
            , _configinfo.InstallPath
            , _configinfo.ClientVersion
            , _configinfo.LastVersion
            , _configinfo.UpdateLogUrl
            , _configinfo.Encoding
            , _configinfo.Format
            , _configinfo.DownloadTimeOut
            , _configinfo.AppSecretKey
            , mainResp.Body);
        _configinfo.ProcessInfo = JsonSerializer.Serialize(processInfo);
    }

    /// <summary>
    ///     User decides if update is required.
    /// </summary>
    /// <returns>is false to continue execution.</returns>
    private bool CanSkip(bool isForcibly)
    {
        if (isForcibly) return false;
        return _customSkipOption?.Invoke() == true;
    }

    /// <summary>
    /// Performs all injected custom operations.
    /// </summary>
    /// <returns></returns>
    private void ExecuteCustomOptions()
    {
        if (!_customOptions.Any()) return;

        foreach (var option in _customOptions)
        {
            if (!option.Invoke())
            {
                EventManager.Instance.Dispatch(this,
                    new ExceptionEventArgs(null, $"{nameof(option)}Execution failure!"));
            }
        }
    }

    /// <summary>
    ///     Clear the environment variable information needed to start the upgrade assistant process.
    /// </summary>
    private void ClearEnvironmentVariable()
    {
        try
        {
            Environment.SetEnvironmentVariable("ProcessInfo", null, EnvironmentVariableTarget.User);
        }
        catch (Exception ex)
        {
            EventManager.Instance.Dispatch(this,
                new ExceptionEventArgs(ex,
                    "Error: An unknown error occurred while deleting the environment variable."));
        }
    }

    protected override void ExecuteStrategy()
    {
        _strategy?.Create(_configinfo!);
        _strategy?.Execute();
    }

    protected override GeneralClientBootstrap StrategyFactory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _strategy = new WindowsStrategy();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            _strategy = new LinuxStrategy();
        else
            throw new PlatformNotSupportedException("The current operating system is not supported!");

        return this;
    }
    
    private bool CheckForcibly(List<VersionBodyDTO> versions)
    {
        foreach (var item in versions)
        {
            if (item.IsForcibly == true)
            {
                return true;
            }
        }

        return false;
    }

    private GeneralClientBootstrap AddListener<TArgs>(Action<object, TArgs> callbackAction) where TArgs : EventArgs
    {
        Debug.Assert(callbackAction != null);
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
        StrategyFactory();
        ExecuteStrategy();
    }
    
    #endregion Private Methods
}