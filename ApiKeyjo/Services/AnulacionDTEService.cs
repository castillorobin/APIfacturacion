using System.Text;

public class AnulacionDTEService
{
    private static readonly HttpClient client = new HttpClient();

    public Task<string> EnviarDTE(string dteFirmado, string token, int ambiente)
    {
        var url = "https://apitest.dtes.mh.gob.sv/fesv/anulardte";
        string ambientet = "00";
        if (ambiente == 1)
        {
            ambientet = "01";
            url = "https://api.dtes.mh.gob.sv/fesv/anulardte";
        }

        // Cuerpo de la solicitud según el manual
        var requestBody = new
        {
            ambiente = ambientet, // Pruebas
            idEnvio = 1, // Correlativo simple, ajusta si necesitas
            version = 2, // Ajusta según tu DTE
            documento = dteFirmado, // DTE firmado como string
            codigoGeneracion = Guid.NewGuid().ToString().ToUpper() // UUID en mayúsculas
        };

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json"); // Content-Type se define aquí

        // Depuración: Imprimir el token y el cuerpo
        // Console.WriteLine($"Token enviado: Bearer {token}");
        //Console.WriteLine($"Cuerpo JSON: {json}");

        // Configurar encabezados del cliente (solo los que no son de contenido)
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("Authorization", $"{token}"); // Token en el encabezado
        client.DefaultRequestHeaders.Add("Accept", "application/json"); // Indica que esperamos JSON como respuesta
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0"); // Opcional, pero recomendado

        try
        {
            var response = client.PostAsync(url, content).Result;  // AQUI SE DETIENE
            var responseData = response.Content.ReadAsStringAsync();

            // Depuración: Imprimir respuesta
            //  Console.WriteLine($"Status Code: {response.StatusCode}");
            // Console.WriteLine($"Respuesta: {responseData}");

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
} // fin


