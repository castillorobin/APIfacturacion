# Etapa de construcción
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copia solo el archivo .csproj principal (ignora .user)
COPY ["ApiKeyjo.csproj", "."]
RUN dotnet restore "ApiKeyjo.csproj"

# Copia el resto de los archivos
COPY . .
RUN dotnet build "ApiKeyjo.csproj" -c Release -o /app/build

# Etapa de publicación
FROM build AS publish
RUN dotnet publish "ApiKeyjo.csproj" -c Release -o /app/publish

# Etapa final
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 7122
ENTRYPOINT ["dotnet", "ApiKeyjo.dll"]