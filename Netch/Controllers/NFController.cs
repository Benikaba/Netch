﻿using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace Netch.Controllers
{
    public class NFController
    {
        /// <summary>
        ///     流量变动事件
        /// </summary>
        public event BandwidthUpdateHandler OnBandwidthUpdated;

        /// <summary>
        ///     流量变动处理器
        /// </summary>
        /// <param name="upload">上传</param>
        /// <param name="download">下载</param>
        public delegate void BandwidthUpdateHandler(long upload, Int64 download);

        /// <summary>
        ///     进程实例
        /// </summary>
        public Process Instance;

        /// <summary>
        ///     当前状态
        /// </summary>
        public Models.State State = Models.State.Waiting;

        /// <summary>
        ///		启动
        /// </summary>
        /// <param name="server">服务器</param>
        /// <param name="mode">模式</param>
        /// <returns>是否成功</returns>
        public bool Start(Models.Server server, Models.Mode mode)
        {
            if (!File.Exists("bin\\Redirector.exe"))
            {
                return false;
            }

            // 生成驱动文件路径
            var driver = String.Format("{0}\\drivers\\netfilter2.sys", Environment.SystemDirectory);

            // 检查驱动是否存在
            if (!File.Exists(driver))
            {
                // 生成系统版本
                var version = $"{Environment.OSVersion.Version.Major.ToString()}.{Environment.OSVersion.Version.Minor.ToString()}";

                // 检查系统版本并复制对应驱动
                try
                {
                    switch (version)
                    {
                        case "10.0":
                            File.Copy("bin\\Win-10.sys", driver);
                            Utils.Logging.Info("已复制 Win10 驱动");
                            break;
                        case "6.3":
                        case "6.2":
                            File.Copy("bin\\Win-8.sys", driver);
                            Utils.Logging.Info("已复制 Win8 驱动");
                            break;
                        case "6.1":
                        case "6.0":
                            File.Copy("bin\\Win-7.sys", driver);
                            Utils.Logging.Info("已复制 Win7 驱动");
                            break;
                        default:
                            Utils.Logging.Info($"不支持的系统版本：{version}");
                            return false;
                    }
                }
                catch (Exception e)
                {
                    Utils.Logging.Info("复制驱动文件失败");
                    Utils.Logging.Info(e.ToString());
                    return false;
                }

                // 注册驱动文件
                var result = nfapinet.NFAPI.nf_registerDriver("netfilter2");
                if (result != nfapinet.NF_STATUS.NF_STATUS_SUCCESS)
                {
                    Utils.Logging.Info($"注册驱动失败，返回值：{result}");
                    return false;
                }
            }

            try
            {
                var service = new ServiceController("netfilter2");
                if (service.Status == ServiceControllerStatus.Stopped)
                {
                    service.Start();
                }
            }
            catch (Exception e)
            {
                Utils.Logging.Info(e.ToString());

                var result = nfapinet.NFAPI.nf_registerDriver("netfilter2");
                if (result != nfapinet.NF_STATUS.NF_STATUS_SUCCESS)
                {
                    Utils.Logging.Info($"注册驱动失败，返回值：{result}");
                    return false;
                }
            }

            var processes = "";
            foreach (var proc in mode.Rule)
            {
                processes += proc;
                processes += ",";
            }
            processes = processes.Substring(0, processes.Length - 1);

            Instance = MainController.GetProcess();
            Instance.StartInfo.FileName = "bin\\Redirector.exe";

            var FallBackArg = "";

            if (server.Type != "Socks5")
            {
                FallBackArg = $"-r 127.0.0.1:{Global.Settings.Socks5LocalPort} -p \"{processes}\"";
            }

            else
            {
                var result = Utils.DNS.Lookup(server.Hostname);
                if (result == null)
                {
                    Utils.Logging.Info("无法解析服务器 IP 地址");
                    return false;
                }

                FallBackArg = $"-r {result.ToString()}:{server.Port} -p \"{processes}\"";

                if (!String.IsNullOrWhiteSpace(server.Username) && !String.IsNullOrWhiteSpace(server.Password))
                {
                    FallBackArg += $" -username \"{server.Username}\" -password \"{server.Password}\"";
                } 
            }

            Instance.StartInfo.Arguments = FallBackArg + $" -t {Global.Settings.RedirectorTCPPort}";
            Instance.OutputDataReceived += OnOutputDataReceived;
            Instance.ErrorDataReceived += OnOutputDataReceived;
            State = Models.State.Starting;
            Instance.Start();
            Instance.BeginOutputReadLine();
            Instance.BeginErrorReadLine();
            for (int i = 0; i < 1000; i++)
            {
                Thread.Sleep(10);

                if (State == Models.State.Started)
                {
                    return true;
                }

                if (State == Models.State.Stopped)
                {
                    Utils.Logging.Info($"尝试去除 \"-t {Global.Settings.RedirectorTCPPort}\" 参数启动");
                    Stop();
                    Instance.StartInfo.Arguments = FallBackArg;
                    Global.Settings.RedirectorTCPPort = 2800;
                    Instance.CancelOutputRead();
                    Instance.CancelErrorRead();
                    Instance.Start();
                    Instance.BeginOutputReadLine();
                    Instance.BeginErrorReadLine();

                    if (State == Models.State.Stopped)
                    {
                        Utils.Logging.Info("NF 进程启动失败");
                        Stop();
                        return false;
                    }

                    else
                    {
                        return true;
                    }
                }
            }

            Utils.Logging.Info("NF 进程启动超时");
            Stop();
            return false;
        }

        /// <summary>
        ///		停止
        /// </summary>
        public void Stop()
        {
            try
            {
                if (Instance != null && !Instance.HasExited)
                {
                    Instance.Kill();
                    Instance.WaitForExit();
                }
            }
            catch (Exception e)
            {
                Utils.Logging.Info(e.ToString());
            }
        }

        public void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!String.IsNullOrWhiteSpace(e.Data))
            {
                File.AppendAllText("logging\\redirector.log", String.Format("{0}\r\n", e.Data));

                if (State == Models.State.Starting)
                {
                    if (Instance.HasExited)
                    {
                        State = Models.State.Stopped;
                    }
                    else if (e.Data.Contains("Started"))
                    {
                        State = Models.State.Started;
                    }
                    else if (e.Data.Contains("Failed") || e.Data.Contains("Unable"))
                    {
                        State = Models.State.Stopped;
                    }
                }
                else if (State == Models.State.Started)
                {
                    if (e.Data.StartsWith("[Application][Bandwidth]"))
                    {
                        var splited = e.Data.Replace("[Application][Bandwidth]", "").Trim().Split(',');
                        if (splited.Length == 2)
                        {
                            var uploadSplited = splited[0].Split(':');
                            var downloadSplited = splited[1].Split(':');

                            if (uploadSplited.Length == 2 && downloadSplited.Length == 2)
                            {
                                if (long.TryParse(uploadSplited[1], out long upload) && long.TryParse(downloadSplited[1], out long download))
                                {
                                    Task.Run(() => OnBandwidthUpdated(upload, download));
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}