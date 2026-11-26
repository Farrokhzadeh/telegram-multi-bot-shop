FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY Api/ /app/Api/
COPY BotHost/ /app/BotHost/
COPY JobHost/ /app/JobHost/
COPY SchemaRunner/ /app/SchemaRunner/
COPY --chmod=0555 deploy-sqlite-runtime.sh /app/
COPY --chmod=0444 sqlite-stage-provision.py /app/
USER 10001:10001
ENTRYPOINT ["/app/Api/Platform.Api"]
CMD ["--serve-qualification"]
