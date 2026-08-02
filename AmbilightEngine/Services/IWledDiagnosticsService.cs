using System.Threading.Tasks;

namespace AmbilightEngine.Services;

public interface IWledDiagnosticsService
{
    Task<bool> TestConnectionAsync(string ipAddress);
    Task<WledDiagnosticsResult> GetDiagnosticsAsync(string ipAddress);
}