namespace AmbilightEngine.Core.SystemState
{
    public sealed class EngineStatusInfo
    {
        public EngineRunState State { get; init; }
        public string Message { get; init; } = string.Empty;

        public static EngineStatusInfo Stopped(string message = "Silnik zatrzymany.")
            => new EngineStatusInfo { State = EngineRunState.Stopped, Message = message };

        public static EngineStatusInfo Starting(string message)
            => new EngineStatusInfo { State = EngineRunState.Starting, Message = message };

        public static EngineStatusInfo Running(string message)
            => new EngineStatusInfo { State = EngineRunState.Running, Message = message };

        public static EngineStatusInfo Ambient(string message)
            => new EngineStatusInfo { State = EngineRunState.Ambient, Message = message };

        public static EngineStatusInfo Error(string message)
            => new EngineStatusInfo { State = EngineRunState.Error, Message = message };
    }
}