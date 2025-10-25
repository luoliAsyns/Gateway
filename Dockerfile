# �����׶�
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG COMMON_REPO=https://github.com/luoliAsyns/Common.git

RUN apt-get update && apt-get install -y git && rm -rf /var/lib/apt/lists/*

# 1. ��װ locales ������������ UTF-8 ���룩
RUN apt-get update && apt-get install -y --no-install-recommends \
    locales \
    && rm -rf /var/lib/apt/lists/*

# 2. ���� zh_CN.UTF-8 ���루֧�����ģ�
RUN locale-gen zh_CN.UTF-8

# 3. ���������ڵ�Ĭ�ϱ���Ϊ UTF-8
ENV LANG=zh_CN.UTF-8 \
    LC_ALL=zh_CN.UTF-8 \
    LANGUAGE=zh_CN.UTF-8

    
# ���ù���Ŀ¼Ϊ��Ŀ��Ŀ¼��Dockerfile����Ŀ¼��
WORKDIR /src


# ��¡Common�ֿ�
# ʹ��GitHub token������֤�����⹫���ֿ��API���ƻ����˽�вֿ�
RUN if [ -n "$GITHUB_TOKEN" ]; then \
        git clone https://$GITHUB_TOKEN@$(echo $COMMON_REPO | sed 's/^https:\/\///') Common; \
    else \
        git clone $COMMON_REPO Common; \
    fi
    
RUN mv /src/Common /Common/

COPY . ./GatewayService/

# ȷ������Ŀ¼��ȷָ����Ŀ�ļ�
WORKDIR "/src/GatewayService"


# �Ȼ�ԭ������ȷ�����ҵ�Common��Ŀ
RUN dotnet restore "./GatewayService.csproj"

# ������Ŀ
RUN dotnet build "./GatewayService.csproj" -c $BUILD_CONFIGURATION -o /app/build

# �����׶�
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./GatewayService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# ���н׶�
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "GatewayService.dll"]
