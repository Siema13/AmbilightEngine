using System;
using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AmbilightEngine.Core.Models;
using AmbilightEngine.Core.SystemState;
using MQTTnet;
using MQTTnet.Protocol;

namespace AmbilightEngine.Services
{
    public sealed class MqttControlService : IAsyncDisposable
    {
        private readonly AmbilightSettings settings;
        private readonly AppEngineHost engineHost;
        private readonly Func<IntPtr> windowHandleProvider;
        private readonly SemaphoreSlim commandLock = new(1, 1);

        private MQTTnet.IMqttClient? client;
        private bool disposed;

        public MqttControlService(
            AmbilightSettings settings,
            AppEngineHost engineHost,
            Func<IntPtr> windowHandleProvider)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.engineHost = engineHost ?? throw new ArgumentNullException(nameof(engineHost));
            this.windowHandleProvider = windowHandleProvider ?? throw new ArgumentNullException(nameof(windowHandleProvider));
        }

        public async Task InitializeAsync()
        {
            if (!settings.MqttEnabled)
            {
                return;
            }

            try
            {
                var factory = new MqttClientFactory();
                client = factory.CreateMqttClient();

                client.ConnectedAsync += OnConnectedAsync;
                client.DisconnectedAsync += OnDisconnectedAsync;
                client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

                var optionsBuilder = new MqttClientOptionsBuilder()
                    .WithClientId(BuildClientId())
                    .WithTcpServer(settings.MqttHost, settings.MqttPort)
                    .WithCleanSession();

                if (!string.IsNullOrWhiteSpace(settings.MqttUsername))
                {
                    optionsBuilder.WithCredentials(settings.MqttUsername, settings.MqttPassword);
                }

                var options = optionsBuilder.Build();
                await client.ConnectAsync(options, CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MQTT] Błąd inicjalizacji MQTT: {ex}");
            }
        }

        private async Task OnConnectedAsync(MqttClientConnectedEventArgs args)
        {
            if (client is null)
            {
                return;
            }

            try
            {
                await client.SubscribeAsync(
                    new MqttTopicFilterBuilder()
                        .WithTopic(BuildCommandTopic("power"))
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build());

                await client.SubscribeAsync(
                    new MqttTopicFilterBuilder()
                        .WithTopic(BuildCommandTopic("brightness"))
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build());

                await client.SubscribeAsync(
                    new MqttTopicFilterBuilder()
                        .WithTopic(BuildCommandTopic("profile"))
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build());

                await PublishStatusAsync("connection", "online");
                await PublishStatusAsync("power", engineHost.IsRunning ? "ON" : "OFF");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MQTT] Błąd po połączeniu: {ex}");
            }
        }

        private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("[MQTT] Rozłączono z brokerem.");
            return Task.CompletedTask;
        }

        private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
        {
            if (disposed)
            {
                return;
            }

            string topic = args.ApplicationMessage.Topic ?? string.Empty;
            string payload = args.ApplicationMessage.Payload.IsEmpty
                ? string.Empty
                : Encoding.UTF8.GetString(args.ApplicationMessage.Payload.ToArray());

            await commandLock.WaitAsync();
            try
            {
                if (topic.Equals(BuildCommandTopic("power"), StringComparison.OrdinalIgnoreCase))
                {
                    await HandlePowerCommandAsync(payload);
                }
                else if (topic.Equals(BuildCommandTopic("brightness"), StringComparison.OrdinalIgnoreCase))
                {
                    await HandleBrightnessCommandAsync(payload);
                }
                else if (topic.Equals(BuildCommandTopic("profile"), StringComparison.OrdinalIgnoreCase))
                {
                    await HandleProfileCommandAsync(payload);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MQTT] Błąd obsługi wiadomości '{topic}': {ex}");
            }
            finally
            {
                commandLock.Release();
            }
        }

        private async Task HandlePowerCommandAsync(string payload)
        {
            string command = (payload ?? string.Empty).Trim().ToUpperInvariant();

            switch (command)
            {
                case "ON":
                    if (!engineHost.IsRunning)
                    {
                        bool started = await engineHost.StartAsync(windowHandleProvider());
                        await PublishStatusAsync("power", started ? "ON" : "OFF");
                    }
                    else
                    {
                        await PublishStatusAsync("power", "ON");
                    }
                    break;

                case "OFF":
                    if (engineHost.IsRunning)
                    {
                        engineHost.Stop();
                    }

                    await PublishStatusAsync("power", "OFF");
                    break;

                case "TOGGLE":
                    if (engineHost.IsRunning)
                    {
                        engineHost.Stop();
                        await PublishStatusAsync("power", "OFF");
                    }
                    else
                    {
                        bool started = await engineHost.StartAsync(windowHandleProvider());
                        await PublishStatusAsync("power", started ? "ON" : "OFF");
                    }
                    break;
            }
        }

        private Task HandleBrightnessCommandAsync(string payload)
        {
            if (!int.TryParse(payload?.Trim(), out int brightness))
            {
                return Task.CompletedTask;
            }

            brightness = Math.Clamp(brightness, 0, 100);
            settings.DefaultProfile.BrightnessPercent = brightness;
            engineHost.ApplyLiveColorCalibration();

            return PublishStatusAsync("brightness", brightness.ToString());
        }

        private Task HandleProfileCommandAsync(string payload)
        {
            string requestedProfile = (payload ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requestedProfile))
            {
                return Task.CompletedTask;
            }

            AppProfile? profile = null;

            if (settings.DefaultProfile != null &&
                string.Equals(settings.DefaultProfile.DisplayName, requestedProfile, StringComparison.OrdinalIgnoreCase))
            {
                profile = settings.DefaultProfile;
            }
            else if (settings.Profiles != null)
            {
                profile = settings.Profiles.Find(p =>
                    string.Equals(p.DisplayName, requestedProfile, StringComparison.OrdinalIgnoreCase));
            }

            if (profile == null)
            {
                return Task.CompletedTask;
            }

            engineHost.ActivateProfile(profile, "MQTT");
            return PublishStatusAsync("profile", profile.DisplayName ?? "unknown");
        }

        public Task PublishPowerStateAsync(bool isRunning)
        {
            return PublishStatusAsync("power", isRunning ? "ON" : "OFF");
        }

        public Task PublishProfileStateAsync(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                return Task.CompletedTask;
            }

            return PublishStatusAsync("profile", profileName);
        }

        private async Task PublishStatusAsync(string suffix, string payload)
        {
            if (client is null || !client.IsConnected)
            {
                return;
            }

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(BuildStatusTopic(suffix))
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(settings.MqttRetainStatus)
                .Build();

            await client.PublishAsync(message, CancellationToken.None);
        }

        private string BuildClientId()
        {
            if (!string.IsNullOrWhiteSpace(settings.MqttClientId))
            {
                return settings.MqttClientId;
            }

            return $"AmbilightEngine-{Environment.MachineName}";
        }

        private string BuildCommandTopic(string suffix) => $"{NormalizePrefix()}/command/{suffix}";
        private string BuildStatusTopic(string suffix) => $"{NormalizePrefix()}/status/{suffix}";

        private string NormalizePrefix()
        {
            string prefix = settings.MqttTopicPrefix?.Trim() ?? "ambilight";
            prefix = prefix.Trim('/');
            return string.IsNullOrWhiteSpace(prefix) ? "ambilight" : prefix;
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            try
            {
                if (client is not null)
                {
                    if (client.IsConnected)
                    {
                        try
                        {
                            await PublishStatusAsync("connection", "offline");
                        }
                        catch
                        {
                        }

                        await client.DisconnectAsync();
                    }

                    client.Dispose();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MQTT] Błąd podczas DisposeAsync: {ex}");
            }

            commandLock.Dispose();
        }
    }
}