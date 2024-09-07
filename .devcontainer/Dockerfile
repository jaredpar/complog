FROM mcr.microsoft.com/devcontainers/dotnet:8.0
ARG USERNAME

# Create a user with the specified username and add to sudo group
RUN useradd -m $USERNAME && \
    echo "$USERNAME ALL=(ALL) NOPASSWD:ALL" >> /etc/sudoers

# Switch to the newly created user
USER $USERNAME

RUN wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh && \
    chmod +x dotnet-install.sh && \
    ./dotnet-install.sh --channel 9.0 --version latest --quality preview --install-dir /usr/share/dotnet && \
    rm dotnet-install.sh

