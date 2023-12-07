FROM mcr.microsoft.com/devcontainers/dotnet:1-8.0-bookworm

RUN wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb && \
    sudo dpkg -i packages-microsoft-prod.deb && \
    rm packages-microsoft-prod.deb

RUN sudo apt-get update && \
    sudo apt-get install -y dotnet-sdk-7.0