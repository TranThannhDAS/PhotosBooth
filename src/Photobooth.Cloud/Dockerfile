# syntax=docker/dockerfile:1
# Chỉ build Cloud, không đưa Desktop WPF vào image Linux.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Photobooth.Cloud.csproj ./
RUN dotnet restore Photobooth.Cloud.csproj
COPY . ./
RUN dotnet publish Photobooth.Cloud.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Tài khoản không đặc quyền của image .NET chính thức.
USER $APP_UID
ENTRYPOINT ["dotnet", "Photobooth.Cloud.dll"]
