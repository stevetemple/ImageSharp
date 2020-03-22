FROM mcr.microsoft.com/dotnet/core/sdk:3.1.200-bionic-arm64v8
MAINTAINER Brian Popow <popow@gmx.de>

ENV TERM xterm

RUN apt-get update
RUN apt-get -y install libgdiplus libgif-dev autoconf libtool automake build-essential gettext libglib2.0-dev libcairo2-dev libtiff-dev libexif-dev
RUN apt-get install nano

COPY . /app/
WORKDIR /app/
RUN dotnet restore
CMD ["dotnet", "test", "tests/ImageSharp.Tests/ImageSharp.Tests.csproj", "-c", "Release", "-f", "netcoreapp3.1"]