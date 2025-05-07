FROM mcr.microsoft.com/dotnet/sdk:8.0

# Install the SignalR Emulator as a global tool
RUN dotnet tool install -g Microsoft.Azure.SignalR.Emulator

# Add the global tools directory to PATH
ENV PATH="$PATH:/root/.dotnet/tools"

EXPOSE 8888

ENTRYPOINT ["asrs-emulator", "start"]
