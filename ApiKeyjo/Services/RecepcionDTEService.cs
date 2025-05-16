using Newtonsoft.Json;
using System.Text;

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

        HttpResponseMessage response = null;
        string responseData = null;

        try
        {
            response = await client.PostAsync("/fesv/recepciondte", content);
            responseData = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                return responseData;
            }

            // Si llega aquí, la respuesta no fue exitosa
            var errorMessage = $"Error en el envío del DTE. Estado: {response.StatusCode}. Respuesta: {responseData}";
            throw new HttpRequestException(errorMessage, null, response.StatusCode);
        }
        catch (HttpRequestException httpEx)
        {
            // Para errores HTTP (incluyendo respuestas no exitosas)
            var errorDetails = new
            {
                Error = httpEx.Message,
                StatusCode = httpEx.StatusCode,
                Response = responseData,
                RequestBody = requestBody
            };

            throw new Exception(JsonConvert.SerializeObject(errorDetails, Formatting.Indented), httpEx);
        }
        catch (Exception ex)
        {
            // Para otros tipos de errores
            var errorDetails = new
            {
                Error = ex.Message,
                Response = responseData,
                RequestBody = requestBody
            };

            throw new Exception(JsonConvert.SerializeObject(errorDetails, Formatting.Indented), ex);
        }
    }
}