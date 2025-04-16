using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
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



app.MapPost("/api/generarpruebasdte", async (
    HttpContext context,
    IHttpClientFactory clientFactory) =>
{
    try
    {
        // Leer los datos de la solicitud como un objeto dinámico
        var requestData = await context.Request.ReadFromJsonAsync<JsonElement>();
        if (requestData.ValueKind != JsonValueKind.Object)
            return Results.BadRequest("Solicitud inválida");

        // Extraer el tipo de DTE
        if (!requestData.TryGetProperty("TipoDte", out var tipoDteElement) || tipoDteElement.ValueKind != JsonValueKind.String)
            return Results.BadRequest("Tipo de DTE no especificado");

        string tipoDte = tipoDteElement.GetString();

        if (!new[] { "01", "03", "11", "14", "05" }.Contains(tipoDte))
            return Results.BadRequest("Tipo de DTE inválido. Valores permitidos: 01, 03, 11, 14, 05");

        // Convertir el JSON a string para pasarlo en las solicitudes
        string jsonContent = System.Text.Json.JsonSerializer.Serialize(requestData);

        var client = clientFactory.CreateClient();
        var resultados = new List<object>();
        var tareasProcesamiento = new List<Task<HttpResponseMessage>>();
        //string usuario = ""; 
        //string password = "";
        //string ambiente="";
        string dteJson = "";
        //string nit = "";
        //string passwordPrivado = "";
        //string codigoGeneracion = "";
        //string numControl = "";
        //string versionDte = "";
        // Lanzar 90 llamadas en paralelo al endpoint existente

        requestData.TryGetProperty("Usuario", out var Usuario);
        requestData.TryGetProperty("Password", out var Password);
        requestData.TryGetProperty("Ambiente", out var Ambiente);
        requestData.TryGetProperty("Nit", out var Nit);
        requestData.TryGetProperty("PasswordPrivado", out var PasswordPrivado);
        requestData.TryGetProperty("VersionDte", out var VersionDte);
        requestData.TryGetProperty("NRC", out var NRC);
        requestData.TryGetProperty("fecha", out var fecha);


        // para nota de credito 
        // Declaración de variables
        string codGeneracion = string.Empty;
        int vueltas = 90;
        int inicio = 1000;
        if (requestData.TryGetProperty("codgeneracion", out var codgeneracion) &&
            requestData.TryGetProperty("vueltas", out var vueltas1))
        {
            codGeneracion = codgeneracion.GetString(); // Asignación de valor string
            if (vueltas1.ValueKind == JsonValueKind.Number && vueltas1.TryGetInt32(out vueltas))
            {
                // Asignación de valor int ya realizada
            }
        }
        requestData.TryGetProperty("inicio", out var inicio1);

         if (inicio1.ValueKind == JsonValueKind.Number && inicio1.TryGetInt32(out inicio))
        {
            // Asignación de valor int ya realizada
        }
        int limite=inicio+ vueltas;

        for (int i = inicio; i < limite; i++)
        {
            Guid codigoGeneracion = Guid.NewGuid();
            string ncontrol = "";


            string correlativo = i.ToString().PadLeft(15, '0');
            // Generar número de control
            ncontrol = "DTE-" + tipoDte + "-" + "ABCD1234" + "-" + correlativo;

            if (tipoDte == "01")
            {
                // Crear el objeto para identificación
                var identificacion = new
                {
                    version = 1,
                    ambiente = "00",
                    tipoDte = tipoDte,
                    numeroControl = ncontrol,
                    codigoGeneracion = codigoGeneracion.ToString().ToUpper(),
                    tipoModelo = 1,
                    tipoOperacion = 1,
                    tipoContingencia = (string)null,
                    motivoContin = (string)null,
                    fecEmi = DateTime.Now.ToString("yyyy-MM-dd"),
                    horEmi = DateTime.Now.ToString("HH:mm:ss"),
                    tipoMoneda = "USD"
                };

                // Crear el objeto para el emisor
                var emisor = new
                {
                    nit = Nit.GetString(),
                    nrc = NRC.GetString(),
                    nombre = "EMPRESA",
                    codActividad = "46510",
                    descActividad = "VENTA AL POR MAYOR DE ARTICULOS DE FERRETERIA Y PINTURERIAS",
                    nombreComercial = "Empresa, S.A. DE C.V.",
                    tipoEstablecimiento = "02",
                    direccion = new
                    {
                        departamento = "04",
                        municipio = "35",
                        complemento = "CARRETERA TRONCAL DEL NORTE KM. 48 1/2, EL COYOLITO, TEJUTLA, CHALATENANGO"
                    },
                    telefono = "2309-3642",
                    codEstableMH = (string)null,
                    codEstable = "F001",
                    codPuntoVentaMH = (string)null,
                    codPuntoVenta = "C001",
                    correo = "empresa1234@gmail.com"
                };

                // Crear el objeto para el receptor
                var receptor = new
                {
                    tipoDocumento = "37",
                    numDocumento = (string)null,
                    nrc = (string)null,
                    nombre = "CLIENTE CASUAL",
                    codActividad = (string)null,
                    descActividad = (string)null,
                    direccion = new
                    {
                        departamento = "05",
                        municipio = "01",
                        complemento = "COYOLITO"
                    },
                    telefono = (string)null,
                    correo = "clientesvarios@dteelsalvadorclientessv.com"
                };

                // Crear el cuerpo del documento
                var cuerpoDocumento = new[]
                {
    new
    {
        numItem = 1,
        tipoItem = 1,
        numeroDocumento = (string)null,
        cantidad = 2.0,
        codigo = "23",
        codTributo = (string)null,
        uniMedida = 59,
        descripcion = "000007 TUBO DE POLIFLEX  GRIS LANCO",
        precioUni = 8.7,
        montoDescu = 0.0,
        ventaNoSuj = 0.0,
        ventaExenta = 0.0,
        ventaGravada = 17.4,
        tributos = (string)null,
        psv = 17.4,
        noGravado = 0.0,
        ivaItem = 2.00176991
    }
};

                // Crear el resumen
                var resumen = new
                {
                    totalNoSuj = 0.00,
                    totalExenta = 0.0,
                    totalGravada = 17.40,
                    subTotalVentas = 17.40,
                    descuNoSuj = 0.0,
                    descuExenta = 0.0,
                    descuGravada = 0.0,
                    porcentajeDescuento = 0,
                    totalDescu = 0.0,
                    tributos = (string)null,
                    subTotal = 17.40,
                    ivaRete1 = 0.00,
                    reteRenta = 0.0,
                    montoTotalOperacion = 17.40,
                    totalNoGravado = 0.0,
                    totalPagar = 17.40,
                    totalLetras = "Diecisiete con 40/100",
                    totalIva = 2.00,
                    saldoFavor = 0.0,
                    condicionOperacion = 1,
                    pagos = new[]
                    {
        new
        {
            codigo = "01",
            montoPago = 17.40,
            referencia = "0000",
            periodo = (string)null,
            plazo = (string)null
        }
    },
                    numPagoElectronico = "0"
                };

                // Crear la extensión
                var extension = new
                {
                    nombEntrega = "ENCARGADO 1",
                    docuEntrega = "00000000-0",
                    nombRecibe = (string)null,
                    docuRecibe = (string)null,
                    observaciones = (string)null,
                    placaVehiculo = (string)null
                };

                // Serializar el objeto JSON completo
                 dteJson = JsonConvert.SerializeObject(new
                {
                    identificacion,
                    documentoRelacionado = (string)null,
                    emisor,
                    receptor,
                    ventaTercero = (string)null,
                    cuerpoDocumento,
                    resumen,
                    extension,
                    otrosDocumentos = (string)null,
                    apendice = (string)null
                }, Formatting.Indented);

                

            }

            if (tipoDte == "03")
            {
                // Crear el objeto para identificación
                var identificacion = new
                {
                    version = 3,
                    ambiente = "00",
                    tipoDte = tipoDte,
                    numeroControl = ncontrol,
                    codigoGeneracion = codigoGeneracion.ToString().ToUpper(),
                    tipoModelo = 1,
                    tipoOperacion = 1,
                    tipoContingencia = (string)null,
                    motivoContin = (string)null,
                    fecEmi = DateTime.Now.ToString("yyyy-MM-dd"),
                    horEmi = DateTime.Now.ToString("HH:mm:ss"),
                    tipoMoneda = "USD"
                };

                // Crear el objeto para el emisor
                var emisor = new
                {
                    nit = Nit.GetString(),
                    nrc = NRC.GetString(),
                    nombre = "EMPRESA",
                    codActividad = "46510",
                    descActividad = "VENTA AL POR MAYOR DE ARTICULOS DE FERRETERIA Y PINTURERIAS",
                    nombreComercial = "Empresa, S.A. DE C.V.",
                    tipoEstablecimiento = "02",
                    direccion = new
                    {
                        departamento = "04",
                        municipio = "35",
                        complemento = "CARRETERA TRONCAL DEL NORTE KM. 48 1/2, EL COYOLITO, TEJUTLA, CHALATENANGO"
                    },
                    telefono = "2309-3642",
                    codEstableMH = (string)null,
                    codEstable = "F001",
                    codPuntoVentaMH = (string)null,
                    codPuntoVenta = "C001",
                    correo = "empresa@gmail.com"
                };

                // Crear el objeto para el receptor
                var receptor = new
                {
                    nit = "06143110171029",
                    nrc = "2649043",
                    nombre = "INDUSTRIAS GRAVABLOCK, SOCIEDAD ANONIMA DE CAPITAL",
                    nombreComercial = (string)null,
                    codActividad = "01460",
                    descActividad = "EXTRACCION DE PIEDRA, ARENA Y ACRILICO",
                    direccion = new
                    {
                        departamento = "05",
                        municipio = "01",
                        complemento = "CARR.TRONCAL DEL NORTE KM.53 1/2, CTON.AGUAJE ESCONDIDO, TEJUTLA, CHALATENANGO"
                    },
                    telefono = "23479900",
                    correo = "jguardadosv@gmail.com"
                };

                // Crear el cuerpo del documento
                var cuerpoDocumento = new[]
                {
        new
        {
            numItem = 1,
            tipoItem = 1,
            numeroDocumento = (string)null,
            cantidad = 10.0,
            codigo = "24",
            codTributo = (string)null,
            uniMedida = 59,
            descripcion = "000008 ACRILICO PAINTERS LANCO ULTRA BLANCO",
            precioUni = 3.185841,
            montoDescu = 0.0,
            ventaNoSuj = 0.0,
            ventaExenta = 0.0,
            ventaGravada = 31.858410,
            tributos = new[] { "20" },
            psv = 31.858410,
            noGravado = 0.0
        }
    };

                // Crear el resumen
                var resumen = new
                {
                    totalNoSuj = 0.00,
                    totalExenta = 0.0,
                    totalGravada = 31.86,
                    subTotalVentas = 31.86,
                    descuNoSuj = 0.0,
                    descuExenta = 0.0,
                    descuGravada = 0.0,
                    porcentajeDescuento = 0,
                    totalDescu = 0.0,
                    tributos = new[]
                    {
            new
            {
                codigo = "20",
                descripcion = "Impuesto al Valor Agregado 13%",
                valor = 4.14
            }
        },
                    subTotal = 31.86,
                    ivaRete1 = 0.00,
                    ivaPerci1 = 0.00,
                    reteRenta = 0.0,
                    montoTotalOperacion = 36.00,
                    totalNoGravado = 0.0,
                    totalPagar = 36.00,
                    totalLetras = "Treinta y seis exactos",
                    saldoFavor = 0.0,
                    condicionOperacion = 1,
                    pagos = new[]
                    {
            new
            {
                codigo = "01",
                montoPago = 36.00,
                referencia = "0000",
                periodo = (string)null,
                plazo = (string)null
            }
        },
                    numPagoElectronico = "0"
                };

                // Crear la extensión
                var extension = new
                {
                    nombEntrega = "ENCARGADO 1",
                    docuEntrega = "00000000-0",
                    nombRecibe = (string)null,
                    docuRecibe = (string)null,
                    observaciones = (string)null,
                    placaVehiculo = (string)null
                };

                // Serializar el objeto JSON completo
                dteJson = JsonConvert.SerializeObject(new
                {
                    identificacion,
                    documentoRelacionado = (string)null,
                    emisor,
                    receptor,
                    ventaTercero = (string)null,
                    cuerpoDocumento,
                    resumen,
                    extension,
                    otrosDocumentos = (string)null,
                    apendice = (string)null
                }, Formatting.Indented);
            }

            if (tipoDte == "11")
            {
                // Crear el objeto para identificación
                var identificacion = new
                {
                    version = 1,
                    ambiente = "00",
                    tipoDte = tipoDte,
                    numeroControl = ncontrol,
                    codigoGeneracion = codigoGeneracion.ToString().ToUpper(),
                    tipoModelo = 1,
                    tipoOperacion = 1,
                    tipoContingencia = (string)null,
                    motivoContigencia = (string)null,
                    fecEmi = DateTime.Now.ToString("yyyy-MM-dd"),
                    horEmi = DateTime.Now.ToString("HH:mm:ss"),
                    tipoMoneda = "USD"
                };

                // Crear el objeto para el emisor
                var emisor = new
                {
                    tipoItemExpor = 1,
                    recintoFiscal = "21",
                    regimen = "EX-1.1000.000",
                    nit = Nit.GetString(),
                    nrc = NRC.GetString(),
                    nombre = "EMPRESA, S.A. DE C.V.",
                    codActividad = "46510",
                    descActividad = "VENTA AL POR MAYOR DE ARTICULOS DE FERRETERIA Y PINTURERIAS",
                    nombreComercial = "EMPRESA, S.A. DE C.V.",
                    tipoEstablecimiento = "02",
                    direccion = new
                    {
                        departamento = "04",
                        municipio = "35",
                        complemento = "CARRETERA TRONCAL DEL NORTE KM. 48 1/2, EL COYOLITO, TEJUTLA, CHALATENANGO"
                    },
                    telefono = "2309-3642",
                    codEstableMH = (string)null,
                    codEstable = "F001",
                    codPuntoVentaMH = (string)null,
                    codPuntoVenta = "C001",
                    correo = "ferreconstruc21@gmail.com"
                };

                // Crear el objeto para el receptor
                var receptor = new
                {
                    nombre = "CLIENTE CASUAL",
                    tipoDocumento = "37",
                    numDocumento = "4561236987",
                    nombreComercial = "CLIENTE CASUAL",
                    codPais = "US",
                    nombrePais = "Estados Unidos",
                    complemento = "COYOLITO",
                    tipoPersona = 2,
                    descActividad = "CULTIVO DE ARROZ",
                    telefono = "78454578",
                    correo = "jguardadosv@gmail.com"
                };

                // Crear el cuerpo del documento
                var cuerpoDocumento = new[]
                {
        new
        {
            numItem = 1,
            cantidad = 10.0,
            codigo = "2462",
            uniMedida = 59,
            descripcion = "002428 METRO DE GRAVA 3/4\"",
            precioUni = 29.0,
            montoDescu = 0.0,
            ventaGravada = 290.0,
            tributos = new[] { "C3" },
            noGravado = 0.0
        }
    };

                // Crear el resumen
                var resumen = new
                {
                    totalGravada = 290.00,
                    descuento = 0.0,
                    porcentajeDescuento = 0,
                    totalDescu = 0.0,
                    seguro = 0.0,
                    flete = 0.0,
                    montoTotalOperacion = 290.0,
                    totalNoGravado = 0.0,
                    totalPagar = 290.0,
                    totalLetras = "Doscientos noventa exactos",
                    condicionOperacion = 1,
                    pagos = new[]
                    {
            new
            {
                codigo = "01",
                montoPago = 290.00,
                referencia = "0000",
                periodo = (string)null,
                plazo = (string)null
            }
        },
                    codIncoterms = (string)null,
                    descIncoterms = (string)null,
                    numPagoElectronico = "0",
                    observaciones = (string)null
                };

               

                // Serializar el objeto JSON completo
                 dteJson = JsonConvert.SerializeObject(new
                {
                    identificacion,
                    emisor,
                    receptor,
                    ventaTercero = (string)null,
                    cuerpoDocumento,
                    resumen,
                    otrosDocumentos = (string)null,
                    apendice = (string)null
                }, Formatting.Indented);
            }

            if (tipoDte == "14")
            {
                var identificacion = new
                {
                    version = 1,
                    ambiente = "00",
                    tipoDte = tipoDte,
                    numeroControl = ncontrol,
                    codigoGeneracion = codigoGeneracion.ToString().ToUpper(),
                    tipoModelo = 1,
                    tipoOperacion = 1,
                    tipoContingencia = (string)null,
                    motivoContin = (string)null,
                    fecEmi = DateTime.Now.ToString("yyyy-MM-dd"),
                    horEmi = DateTime.Now.ToString("HH:mm:ss"),
                    tipoMoneda = "USD"
                };

                var emisor = new
                {
                    nit = Nit.GetString(),
                    nrc = NRC.GetString(),
                    nombre = "EMPRESA, S.A. DE C.V.",
                    codActividad = "46510",
                    descActividad = "VENTA AL POR MAYOR DE ARTICULOS DE FERRETERIA Y PINTURERIAS",
                    direccion = new
                    {
                        departamento = "04",
                        municipio = "35",
                        complemento = "CARRETERA TRONCAL DEL NORTE KM. 48 1/2, EL COYOLITO, TEJUTLA, CHALATENANGO"
                    },
                    telefono = "2309-3642",
                    codEstableMH = (string?)null,
                    codEstable = "F001",
                    codPuntoVentaMH = (string?)null,
                    codPuntoVenta = "C001",
                    correo = "empresa@gmail.com"
                };

                var sujetoExcluido = new
                {
                    tipoDocumento = "13",
                    numDocumento = "008614277",
                    nombre = "YESENIA JUDITH ZEPEDA DE MIRANDA",
                    codActividad = (string?)null,
                    descActividad = (string?)null,
                    direccion = new
                    {
                        departamento = "05",
                        municipio = "01",
                        complemento = "CALLE PTE 17 AV. NORTE, #17, COM LAS AMERICAS, SAN SALVADOR"
                    },
                    telefono = "62053168",
                    correo = "facturacion@dteelsalvador.info"
                };

                var cuerpoDocumento = new[]
                {
        new
        {
            numItem = 1,
            tipoItem = 1,
            cantidad = 10,
            codigo = "0000",
            uniMedida = 59,
            descripcion = "SOPAS",
            precioUni = 1.00,
            montoDescu = 0.0,
            compra = 10.00
        }
    };

                var resumen = new
                {
                    totalCompra = 10.00,
                    descu = 0.0,
                    totalDescu = 0.0,
                    subTotal = 10.00,
                    ivaRete1 = 0.0,
                    reteRenta = 0.0,
                    totalPagar = 10.00,
                    totalLetras = "Diez exactos",
                    condicionOperacion = 1,
                    pagos = new[]
                    {
            new
            {
                codigo = "01",
                montoPago = 10.00,
                referencia = "0000",
                periodo = (string?)null,
                plazo = (string?)null
            }
        },
                    observaciones = (string?)null
                };

                 dteJson = JsonConvert.SerializeObject(new
                {
                    identificacion,
                    emisor,
                    sujetoExcluido,
                    cuerpoDocumento,
                    resumen,
                    apendice = (string?)null
                }, Formatting.Indented);
            }

            if (tipoDte == "05")
            {
                var identificacion = new
                {
                    version = 3,
                    ambiente = "00",
                    tipoDte = tipoDte,
                    numeroControl = ncontrol,
                    codigoGeneracion = codigoGeneracion.ToString().ToUpper(),
                    tipoModelo = 1,
                    tipoOperacion = 1,
                    tipoContingencia = (string)null,
                    motivoContin = (string?)null,
                    fecEmi = DateTime.Now.ToString("yyyy-MM-dd"),
                    horEmi = DateTime.Now.ToString("HH:mm:ss"),
                    tipoMoneda = "USD"
                };

                var documentoRelacionado = new[]
                {
        new
        {
            tipoDocumento = "03",
            tipoGeneracion = 2,
            numeroDocumento = codGeneracion,
            fechaEmision = fecha.ToString()
        }
    };

                var emisor = new
                {
                    nit = Nit.GetString(),
                    nrc = NRC.GetString(),
                    nombre = "EMPRESA",
                    codActividad = "46510",
                    descActividad = "VENTA AL POR MAYOR DE ARTICULOS DE FERRETERIA Y PINTURERIAS",
                    nombreComercial = "EMPRESA",
                    tipoEstablecimiento = "02",
                    direccion = new
                    {
                        departamento = "04",
                        municipio = "35",
                        complemento = "CARRETERA TRONCAL DEL NORTE KM. 48 1/2, EL COYOLITO, TEJUTLA, CHALATENANGO"
                    },
                    telefono = "2309-3642",
                    correo = "empresa@email.com"
                };

                var receptor = new
                {
                    nit = "06143110171029",
                    nrc = "2649043",
                    nombre = "ROBERTO ANTONIO CASTILLO ALAS",
                    nombreComercial = (string?)null,
                    codActividad = "01460",
                    descActividad = "ACTIVIDAD INMOBILIARIAS, REALIZADAS CON BIENES PROPIOS O ARRENDADOS",
                    direccion = new
                    {
                        departamento = "05",
                        municipio = "01",
                        complemento = " EL COYOLITO, A1 KM DE LA GRANJA"
                    },
                    telefono = "75265918",
                    correo = "jguardadosv@gmail.com"
                };

                var cuerpoDocumento = new[]
                {
        new
        {
            numItem = 1,
            tipoItem = 3,
            numeroDocumento = codGeneracion,
            cantidad = 1,
            codigo = "000002",
            codTributo = (string?)null,
            uniMedida = 59,
            descripcion = "MASILLA PARA MADERA CEDAR DE 118.5 ML",
            precioUni = 1.00,
            montoDescu = 0.0,
            ventaNoSuj = 0.0,
            ventaExenta = 0.0,
            ventaGravada = 1.00,
            tributos = new[] { "20" }
        }
    };

                var resumen = new
                {
                    totalNoSuj = 0.0,
                    totalExenta = 0.0,
                    totalGravada = 1.00,
                    subTotalVentas = 1.00,
                    descuNoSuj = 0.0,
                    descuExenta = 0.0,
                    descuGravada = 0.0,
                    totalDescu = 0.0,
                    tributos = new[]
                    {
            new
            {
                codigo = "20",
                descripcion = "Impuesto al Valor Agregado 13%",
                valor = 0.13
            }
        },
                    subTotal = 1.00,
                    ivaPerci1 = 0.0,
                    ivaRete1 = 0.0,
                    reteRenta = 0.0,
                    montoTotalOperacion = 1.13,
                    totalLetras = "Tres con 60/100",
                    condicionOperacion = 1
                };

                var extension = new
                {
                    nombEntrega = "ENCARGADO 1",
                    docuEntrega = "00000000-0",
                    nombRecibe = (string?)null,
                    docuRecibe = (string?)null,
                    observaciones = (string?)null
                };

                 dteJson = JsonConvert.SerializeObject(new
                {
                    identificacion,
                    documentoRelacionado,
                    emisor,
                    receptor,
                    cuerpoDocumento,
                    resumen,
                    extension,
                    apendice = (string?)null,
                     ventaTercero = (string?)null
                 }, Formatting.Indented);
            }


            var dteRequestCompleto = new
            {
                Usuario = Usuario,
                Password = Password,
                Ambiente = "00",
                DteJson = dteJson, // Coloca tu JSON completo aquí
                Nit = Nit,
                PasswordPrivado = PasswordPrivado,
                TipoDte = tipoDte, // Usamos el tipo que viene en la solicitud
                CodigoGeneracion = codigoGeneracion.ToString().ToUpper(),
                NumControl = ncontrol,
                VersionDte = VersionDte.GetString()
            };

            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            var tarea = client.PostAsJsonAsync("http://207.58.175.219:7122/api/procesar-dte", dteRequestCompleto);
            tareasProcesamiento.Add(tarea);
        }

        // Esperar a que todas las tareas se completen
        var respuestas = await Task.WhenAll(tareasProcesamiento);

        // Procesar resultados
        for (int i = 0; i < respuestas.Length; i++)
        {
            var respuesta = respuestas[i];
            var contenido = await respuesta.Content.ReadAsStringAsync();

            if (respuesta.IsSuccessStatusCode)
            {
                resultados.Add(new
                {
                    Indice = i + 1,
                    Exitoso = true,
                    Respuesta = JsonDocument.Parse(contenido).RootElement
                });
            }
            else
            {
                resultados.Add(new
                {
                    Indice = i + 1,
                    Exitoso = false,
                    Error = contenido
                });
            }
        }

        return Results.Ok(new
        {
            TotalLlamadas = 90,
            TipoDte = tipoDte,
            Resultados = resultados
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Error general: {ex.Message}");
    }
});

app.Run("http://*:7122");


