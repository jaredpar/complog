FROM mcr.microsoft.com/devcontainers/dotnet:9.0

RUN wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh && \
    chmod +x dotnet-install.sh && \
    ./dotnet-install.sh --channel 10.0 --version latest --install-dir /usr/share/dotnet && \
    rm dotnet-install.sh

RUN curl -fsSL https://gh.io/copilot-install | bash
