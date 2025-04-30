using SimHub.Plugins;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Timers;
using Newtonsoft.Json.Linq;
using System.Windows.Controls;
using GameReaderCommon;

namespace LMU_WeatherPlugin
{
    [PluginDescription("LMU Weather Fetcher with Dynamic Node Properties")]
    [PluginAuthor("geims12")]
    [PluginName("LMU_WeatherPlugin")]
    public class LMU_WeatherPlugin : IPlugin, IDataPlugin, IWPFSettings
    {
        private Timer _timer;
        private HttpClient _httpClient;
        private PluginManager _pluginManager;

        private readonly string[] Nodes = new[] { "START", "NODE_25", "NODE_50", "NODE_75", "FINISH" };
        private readonly string[] Metrics = new[]
        {
            "WNV_TEMPERATURE", "WNV_WINDDIRECTION", "WNV_RAIN_CHANCE", "WNV_WINDSPEED",
            "WNV_STARTTIME", "WNV_SKY", "WNV_DURATION", "WNV_HUMIDITY"
        };

        private string _currentSession = "RACE";
        private JObject _weatherData;
        private int _currentSessionLengthMinutes = 0;
        private string _currentNode = "START";
        private bool _timerRunning = false;
        private TimeSpan _lastSessionTimeLeft = TimeSpan.Zero;

        public PluginManager PluginManager
        {
            get => _pluginManager;
            set => _pluginManager = value;
        }

        public void Init(PluginManager pluginManager)
        {
            _pluginManager = pluginManager;
            _httpClient = new HttpClient();

            foreach (var node in Nodes)
            {
                foreach (var metric in Metrics)
                {
                    AttachDelegate($"{node}_{metric}");
                    AttachDelegate($"{node}_{metric}Str");
                }
            }

            AttachDelegate("CurrentSessionLengthMinutes");
            AttachDelegate("CurrentNodeDurationMinutes");
            AttachDelegate("CurrentNodeName");

            foreach (var metric in Metrics)
            {
                AttachDelegate($"CURRENTNODE_{metric}");
                AttachDelegate($"CURRENTNODE_{metric}Str");
            }

            AttachDelegate("TimeUntil_NODE_25");
            AttachDelegate("TimeUntil_NODE_50");
            AttachDelegate("TimeUntil_NODE_75");
            AttachDelegate("TimeUntil_FINISH");

            _timer = new Timer(1000);
            _timer.Elapsed += async (s, e) => await FetchWeather().ConfigureAwait(false);

            SimHub.Logging.Current.Info("[LMU WeatherPlugin] Initialized successfully.");
        }

        private void AttachDelegate(string name)
        {
            this.AttachDelegate(name, () => GetWeatherData(name));
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            bool isLmu = false;
            bool shouldRun = false;

            try
            {
                isLmu = (data.GameName?.ToUpperInvariant() == "LMU");
                shouldRun = data.GameRunning && !data.GameInMenu && !data.GamePaused && isLmu;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"[LMU WeatherPlugin] DataUpdate check failed: {ex.Message}");
            }

            if (shouldRun)
            {
                if (!_timerRunning)
                {
                    _timer.Start();
                    _timerRunning = true;
                    SimHub.Logging.Current.Info("[LMU WeatherPlugin] Game running (LMU) — started weather updates.");
                }

                _currentSession = data.NewData.SessionTypeName?.ToUpperInvariant() ?? "RACE";
                _lastSessionTimeLeft = data.NewData.SessionTimeLeft;
                UpdateCurrentNode(_lastSessionTimeLeft);
            }
            else
            {
                if (_timerRunning)
                {
                    _timer.Stop();
                    _timerRunning = false;
                    SimHub.Logging.Current.Info("[LMU WeatherPlugin] Game not running or not LMU — stopped weather updates.");
                }
            }
        }

        private async Task FetchWeather()
        {
            try
            {
                var urlWeather = $"http://localhost:6397/rest/sessions/weather/{_currentSession}";
                var responseWeather = await _httpClient.GetStringAsync(urlWeather);
                _weatherData = JObject.Parse(responseWeather);

                var urlSessions = "http://localhost:6397/rest/sessions/GetSessionsInfoForEvent";
                var responseSessions = await _httpClient.GetStringAsync(urlSessions);
                var sessionInfo = JObject.Parse(responseSessions);

                var scheduledSessions = sessionInfo["scheduledSessions"];
                if (scheduledSessions != null)
                {
                    foreach (var session in scheduledSessions)
                    {
                        var name = (string)session["name"] ?? "";
                        if (IsMatchingSessionName(name, _currentSession))
                        {
                            _currentSessionLengthMinutes = (int?)session["lengthTime"] ?? 0;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"[LMU WeatherPlugin] Failed to fetch weather data: {ex.Message}");
            }
        }

        private void UpdateCurrentNode(TimeSpan sessionTimeLeft)
        {
            if (_currentSessionLengthMinutes <= 0)
            {
                _currentNode = "START";
                return;
            }

            double totalSessionSeconds = _currentSessionLengthMinutes * 60.0;
            double timeLeftSeconds = sessionTimeLeft.TotalSeconds;
            double elapsedSeconds = totalSessionSeconds - timeLeftSeconds;

            if (elapsedSeconds < 0) elapsedSeconds = 0;
            if (elapsedSeconds > totalSessionSeconds) elapsedSeconds = totalSessionSeconds;

            double progress = elapsedSeconds / totalSessionSeconds;

            if (progress < 0.25)
                _currentNode = "START";
            else if (progress < 0.5)
                _currentNode = "NODE_25";
            else if (progress < 0.75)
                _currentNode = "NODE_50";
            else if (progress < 1.0)
                _currentNode = "NODE_75";
            else
                _currentNode = "FINISH";
        }

        private object GetWeatherData(string name)
        {
            if (name == "CurrentSessionLengthMinutes") return _currentSessionLengthMinutes;
            if (name == "CurrentNodeDurationMinutes") return _currentSessionLengthMinutes / 5.0;
            if (name == "CurrentNodeName") return _currentNode;

            if (name.StartsWith("TimeUntil_"))
                return GetTimeUntilNode(name.Substring("TimeUntil_".Length));

            if (name.StartsWith("CURRENTNODE_"))
                return GetCurrentNodeMetric(name);

            if (_weatherData == null)
            {
                if (name.EndsWith("Str")) return "";
                else return 0.0;
            }

            var isStringValue = name.EndsWith("Str");
            var cleanName = isStringValue ? name.Substring(0, name.Length - 3) : name;

            var parts = cleanName.Split('_');
            if (parts.Length < 2)
            {
                if (isStringValue) return "";
                else return 0.0;
            }

            string node;
            string metric;

            if (parts[0] == "NODE" && parts.Length >= 3)
            {
                node = $"{parts[0]}_{parts[1]}";
                metric = string.Join("_", parts, 2, parts.Length - 2);
            }
            else
            {
                node = parts[0];
                metric = string.Join("_", parts, 1, parts.Length - 1);
            }

            var nodeData = _weatherData?[node];
            var metricData = nodeData?[metric];

            if (metricData == null)
            {
                if (isStringValue) return "";
                else return 0.0;
            }

            if (isStringValue)
                return (string)metricData["stringValue"] ?? "";
            else
                return (double?)metricData["currentValue"] ?? 0.0;
        }

        private object GetCurrentNodeMetric(string name)
        {
            if (_weatherData == null || string.IsNullOrEmpty(_currentNode))
            {
                if (name.EndsWith("Str")) return "";
                else return 0.0;
            }

            var isStringValue = name.EndsWith("Str");
            var cleanMetricName = isStringValue
                ? name.Substring("CURRENTNODE_".Length, name.Length - "CURRENTNODE_".Length - 3)
                : name.Substring("CURRENTNODE_".Length);

            var nodeData = _weatherData[_currentNode];
            var metricData = nodeData?[cleanMetricName];

            if (metricData == null)
            {
                if (isStringValue) return "";
                else return 0.0;
            }

            if (isStringValue)
                return (string)metricData["stringValue"] ?? "";
            else
                return (double?)metricData["currentValue"] ?? 0.0;
        }

        private double GetTimeUntilNode(string nodeName)
        {
            if (_currentSessionLengthMinutes <= 0 || _lastSessionTimeLeft.TotalMinutes <= 0)
                return 0.0;

            double nodeStart = 0.0;
            switch (nodeName)
            {
                case "NODE_25": nodeStart = 0.25; break;
                case "NODE_50": nodeStart = 0.50; break;
                case "NODE_75": nodeStart = 0.75; break;
                case "FINISH": nodeStart = 1.00; break;
                default: return 0.0;
            }

            double elapsedMinutes = _currentSessionLengthMinutes - _lastSessionTimeLeft.TotalMinutes;
            double nodeStartTime = _currentSessionLengthMinutes * nodeStart;

            double minutesLeft = nodeStartTime - elapsedMinutes;
            return minutesLeft > 0 ? minutesLeft : 0.0;
        }

        private bool IsMatchingSessionName(string apiSessionName, string internalSession)
        {
            if (string.IsNullOrEmpty(apiSessionName) || string.IsNullOrEmpty(internalSession))
                return false;

            var apiName = apiSessionName.ToUpperInvariant();
            var internalName = internalSession.ToUpperInvariant();

            return
                (internalName == "PRACTICE" && apiName.Contains("PRACTICE")) ||
                (internalName == "QUALIFY" && apiName.Contains("QUALIFY")) ||
                (internalName == "RACE" && apiName.Contains("RACE"));
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager) => null;

        public void End(PluginManager pluginManager)
        {
            _timer?.Stop();
            _timer?.Dispose();
            _httpClient?.Dispose();
            SimHub.Logging.Current.Info("[LMU WeatherPlugin] Plugin has been stopped.");
        }
    }
}
