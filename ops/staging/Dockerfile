FROM mcr.microsoft.com/dotnet/sdk@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e
WORKDIR /app
COPY --chmod=0555 Platform.Api Platform.BotHost Platform.AotSmoke deploy-shop-runtime.sh /app/
COPY --chmod=0444 shop/ /app/shop/
COPY --chmod=0444 0001_core.sql /app/0001_core.sql
USER 10001:10001
ENTRYPOINT ["/app/Platform.Api"]
CMD ["--serve-qualification"]
