using Newtonsoft.Json;
using System.Text;
// Servicio para enviar el DTE (optimizado)
public class RecepcionDTEService
{
    private readonly IHttpClientFactory _clientFactory;

    public RecepcionDTEService(IHttpClientFactory clientFactory)
    {
        _clientFactory = clientFactory;
    }

    public async Task<string> EnviarDTE(string dteFirmado, string token, string tipodte, string codigogeneracion, int ambienteDTE, int versiondte)
    {
        string ambiente = ambienteDTE == 1 ? "01" : "00";
        string baseUrl = ambienteDTE == 1
            ? "https://api.dtes.mh.gob.sv"
            : "https://apitest.dtes.mh.gob.sv";

        var client = _clientFactory.CreateClient("RecepcionService");
        client.BaseAddress = new Uri(baseUrl);
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Authorization", token);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        var requestBody = new
        {
            ambiente = ambiente,
            idEnvio = 1,
            version = versiondte,
            tipoDte = tipodte,
            documento = dteFirmado,
            codigoGeneracion = codigogeneracion
        };

        var json = JsonConvert.SerializeObject(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await client.PostAsync("/fesv/recepciondte", content);
            var responseData = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                return responseData;
            }
            else
            {
                throw new Exception($"Error en el envío del DTE: {response.StatusCode}. Detalles: {responseData}");
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error al enviar el DTE: {ex.Message}", ex);
        }
    }
}


