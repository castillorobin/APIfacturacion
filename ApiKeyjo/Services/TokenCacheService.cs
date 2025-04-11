using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
// Servicio de caché de tokens
// Servicio de caché de tokens mejorado con verificación de expiración JWT
// Servicio de caché de tokens con requisito estricto de validez
public class TokenCacheService
{
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _clientFactory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new ConcurrentDictionary<string, SemaphoreSlim>();

    public TokenCacheService(IMemoryCache cache, IHttpClientFactory clientFactory)
    {
        _cache = cache;
        _clientFactory = clientFactory;
    }

    public async Task<string> GetTokenAsync(string usuario, string password, string ambiente)
    {
        string cacheKey = $"AuthToken_{usuario}_{ambiente}";

        // Intentar obtener del caché
        if (_cache.TryGetValue(cacheKey, out string cachedToken))
        {
            // Verificar si el token JWT es válido
            if (IsTokenValid(cachedToken))
            {
                return cachedToken;
            }
            // Si no es válido, eliminarlo del caché
            _cache.Remove(cacheKey);
        }

        // Obtener o crear un semáforo para este usuario específico
        var semaphore = _locks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync();
        try
        {
            // Verificar el caché nuevamente después de obtener el semáforo
            if (_cache.TryGetValue(cacheKey, out string token) && IsTokenValid(token))
            {
                return token;
            }

            // Si no está en caché o no es válido, autenticar siempre
            var authUrl = ambiente == "01"
                ? "https://api.dtes.mh.gob.sv/seguridad/auth"
                : "https://apitest.dtes.mh.gob.sv/seguridad/auth";

            using var client = _clientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            client.DefaultRequestHeaders.Add("Accept", "application/json");

            var authResponse = await client.PostAsync(authUrl, new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("user", usuario),
                new KeyValuePair<string, string>("pwd", password)
            }));

            if (!authResponse.IsSuccessStatusCode)
                throw new Exception($"Error en autenticación: {authResponse.StatusCode}");

            var authContent = await authResponse.Content.ReadAsStringAsync();
            token = JsonConvert.DeserializeObject<AuthResponse>(authContent)?.Body?.Token;

            if (string.IsNullOrEmpty(token))
                throw new Exception("Token no recibido");

            // Verificamos que el token recibido sea válido
            if (!IsTokenValid(token))
                throw new Exception("El token recibido no es válido o no se puede verificar");

            // Determinar tiempo de expiración del token JWT y establecer caché con ese tiempo
            // menos un margen de seguridad (por ejemplo, 5 minutos antes)
            TimeSpan cacheTime = GetTokenLifetime(token).Subtract(TimeSpan.FromMinutes(5));

            // Asegurarnos de que el tiempo de caché sea positivo
            if (cacheTime.TotalMinutes <= 0)
                cacheTime = TimeSpan.FromMinutes(1); // Mínimo 1 minuto si ya está por expirar

            _cache.Set(cacheKey, token, cacheTime);

            return token;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private bool IsTokenValid(string token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        try
        {
            // Dividir el token en sus partes (header, payload, signature)
            var parts = token.Split('.');
            if (parts.Length != 3)
                return false; // No es un JWT válido

            // Decodificar el payload
            var payload = parts[1];
            var paddedPayload = payload.PadRight(4 * ((payload.Length + 3) / 4), '=').Replace('-', '+').Replace('_', '/');
            var decodedBytes = Convert.FromBase64String(paddedPayload);
            var jsonPayload = Encoding.UTF8.GetString(decodedBytes);

            // Parsear el payload como JSON
            using (JsonDocument doc = JsonDocument.Parse(jsonPayload))
            {
                // Buscar el claim "exp" (timestamp de expiración)
                if (doc.RootElement.TryGetProperty("exp", out var expClaim))
                {
                    // El exp es un timestamp Unix (segundos desde 1/1/1970)
                    var expDateTime = DateTimeOffset.FromUnixTimeSeconds(expClaim.GetInt64()).UtcDateTime;

                    // Verificar si ha expirado (con un margen de seguridad de 30 segundos)
                    return DateTime.UtcNow.AddSeconds(30) < expDateTime;
                }

                // Si no encuentra el claim "exp", considerarlo inválido
                return false;
            }
        }
        catch
        {
            // Cualquier error al procesar el token, considerarlo inválido
            return false;
        }
    }

    private TimeSpan GetTokenLifetime(string token)
    {
        try
        {
            // Dividir el token en sus partes
            var parts = token.Split('.');
            if (parts.Length != 3)
                return TimeSpan.FromMinutes(5); // Valor mínimo si no es un JWT válido

            // Decodificar el payload
            var payload = parts[1];
            var paddedPayload = payload.PadRight(4 * ((payload.Length + 3) / 4), '=').Replace('-', '+').Replace('_', '/');
            var decodedBytes = Convert.FromBase64String(paddedPayload);
            var jsonPayload = Encoding.UTF8.GetString(decodedBytes);

            // Parsear el payload
            using (JsonDocument doc = JsonDocument.Parse(jsonPayload))
            {
                // Si tiene exp, calcular tiempo restante hasta expiración
                if (doc.RootElement.TryGetProperty("exp", out var expClaim))
                {
                    var expTime = DateTimeOffset.FromUnixTimeSeconds(expClaim.GetInt64()).UtcDateTime;
                    var remaining = expTime - DateTime.UtcNow;

                    // Asegurarnos de que el resultado sea positivo
                    return remaining.TotalMinutes > 0 ? remaining : TimeSpan.FromMinutes(1);
                }

                // Si no se puede determinar, usar un valor mínimo
                return TimeSpan.FromMinutes(5);
            }
        }
        catch
        {
            // En caso de error, usar un valor mínimo
            return TimeSpan.FromMinutes(5);
        }
    }
}


