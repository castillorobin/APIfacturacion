using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;
// Servicio para firmar el DTE (optimizado)
public class FirmaElectronicaService
{
    private readonly HttpClient _client;

    public FirmaElectronicaService(IHttpClientFactory clientFactory, string ambiente="00")
    {
        _client = clientFactory.CreateClient("FirmaService");
        _client.BaseAddress = new Uri("http://207.58.175.219:8113/");
        if (ambiente=="01")
        {
            _client.BaseAddress = new Uri("http://44.204.189.104:8114/");
        }
        
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> FirmarDocumento(string dteJson, string nit, string passwordPri)
    {
        var requestBody = new
        {
            nit = nit,
            activo = true,
            passwordPri = passwordPri,
            dteJson = JsonConvert.DeserializeObject<dynamic>(dteJson)
        };

        var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("firmardocumento/", content);

        if (response.IsSuccessStatusCode)
        {
            var responseData = await response.Content.ReadAsStringAsync();
            return responseData; // Retorna el DTE firmado
        }
        else
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"Error en la firma electrónica: {response.StatusCode}. Detalles: {errorContent}");
        }
    }
}


