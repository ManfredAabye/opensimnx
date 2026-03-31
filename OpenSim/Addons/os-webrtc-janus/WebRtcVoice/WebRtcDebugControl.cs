using System;
using log4net;
using log4net.Core;
using log4net.Repository.Hierarchy;
using Nini.Config;

namespace WebRtcVoice
{
    public static class WebRtcDebugControl
    {
        private static readonly object _Sync = new object();

        public static void ApplyFromConfig(IConfigSource pConfig)
        {
            bool janusDebug = false;
            IConfig janusConfig = pConfig?.Configs["JanusWebRtcVoice"];
            if (janusConfig is not null)
            {
                janusDebug = janusConfig.GetBoolean("JanusDebug", false);
            }

            Apply(janusDebug);
        }

        public static void Apply(bool pJanusDebug)
        {
            lock (_Sync)
            {
                if (LogManager.GetRepository() is not Hierarchy hierarchy)
                {
                    return;
                }

                if (hierarchy.GetLogger("WebRtcVoice") is not Logger namespaceLogger)
                {
                    return;
                }

                namespaceLogger.Level = pJanusDebug ? Level.Debug : Level.Info;
                hierarchy.RaiseConfigurationChanged(EventArgs.Empty);
            }
        }
    }
}