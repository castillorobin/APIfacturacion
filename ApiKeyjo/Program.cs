using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient(); // Registrando IHttpClientFactory
builder.Services.AddMemoryCache(); // Agregar servicio de caché
builder.Services.AddSingleton<TokenCacheService>(); // Registrar servicio de caché de tokens
builder.Services.AddSingleton<FirmaElectronicaService>(); // Registrar como singleton
builder.Services.AddSingleton<RecepcionDTEService>(); // Registrar como singleton
builder.Services.AddSingleton<AnulacionDTEService>(); // Registrar como singleton

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Endpoint optimizado
app.MapPost("/api/procesar-dte", async (
    HttpContext context,
    TokenCacheService tokenService,
    FirmaElectronicaService firmaService,
    RecepcionDTEService recepcionService) =>
{
    try
    {
        var request = await context.Request.ReadFromJsonAsync<DteUnificadoRequest>();
        if (request == null)
            return Results.BadRequest("Solicitud inválida");
        // 1. Obtener token (usando el servicio de caché)
        string token = await tokenService.GetTokenAsync(request.Usuario, request.Password, request.Ambiente);
        if (string.IsNullOrEmpty(token))
            return Results.BadRequest("Error en autenticación: token no recibido");
        // 2. Firma
        string jsonFirmado;
        try
        {
            var dteFirmado = await firmaService.FirmarDocumento(request.DteJson, request.Nit, request.PasswordPrivado);
            jsonFirmado = JsonConvert.DeserializeObject<dynamic>(dteFirmado)?.body.ToString();
            if (string.IsNullOrEmpty(jsonFirmado))
                return Results.BadRequest("Error en firma: documento firmado inválido");
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error en firma de documento: {ex.Message}");
        }
        // 3. Envío
        string respuestaEnvio;
        try
        {
            respuestaEnvio = await recepcionService.EnviarDTE(
                jsonFirmado,
                token,
                request.TipoDte,
                request.CodigoGeneracion,
                request.Ambiente == "01" ? 1 : 0,
                request.VersionDte);
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error en envío de DTE: {ex.Message}");
        }
        // Procesar respuesta
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(respuestaEnvio))
            {
                if (doc.RootElement.TryGetProperty("selloRecibido", out var sello))
                {
                    return Results.Ok(new
                    {
                        Token = token,
                        DteFirmado = jsonFirmado,
                        SelloRecibido = sello.GetString(),
                        CodigoGeneracion = request.CodigoGeneracion,
                        NumControl = request.NumControl
                    });
                }
            }
            return Results.BadRequest("No se recibió sello de Hacienda");
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error procesando respuesta: {ex.Message}");
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Error general: {ex.Message}");
    }
});

app.MapPost("/api/anular-dte", async (
    HttpContext context,
    TokenCacheService tokenService,
    FirmaElectronicaService firmaService,
    AnulacionDTEService anulacionService) =>
{
    try
    {

        var request = await context.Request.ReadFromJsonAsync<DteAnulacionRequest>();
        if (request == null)
            return Results.BadRequest("Solicitud inválida");

        // 1. Obtener token (usando el servicio de caché)
        string token = await tokenService.GetTokenAsync(request.Usuario, request.Password, request.Ambiente);
        if (string.IsNullOrEmpty(token))
            return Results.BadRequest("Error en autenticación: token no recibido");

        // 2. Firma
        string jsonFirmado;
        try
        {
            var dteFirmado = await firmaService.FirmarDocumento(request.DteJson, request.Nit, request.PasswordPrivado);
            jsonFirmado = JsonConvert.DeserializeObject<dynamic>(dteFirmado)?.body.ToString();
            if (string.IsNullOrEmpty(jsonFirmado))
                return Results.BadRequest("Error en firma: documento firmado inválido");
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error en firma de documento: {ex.Message}");
        }

        // 3. Envío de anulación
        string respuestaEnvio;
        try
        {
            int ambienteInt = request.Ambiente == "01" ? 1 : 0;
            respuestaEnvio = await anulacionService.EnviarDTE(jsonFirmado, token, ambienteInt);
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error en envío de DTE (anulación): {ex.Message}");
        }

        // Procesar respuesta
        try
        {
            using (JsonDocument doc = JsonDocument.Parse(respuestaEnvio))
            {
                if (doc.RootElement.TryGetProperty("selloRecibido", out var sello))
                {
                    return Results.Ok(new
                    {
                        Token = token,
                        DteFirmado = jsonFirmado,
                        SelloRecibido = sello.GetString()
                       
                    });
                }
            }
            return Results.BadRequest("No se recibió sello de Hacienda");
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error procesando respuesta: {ex.Message}");
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Error general: {ex.Message}");
    }
});


app.Run("http://*:7122");


