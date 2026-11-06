# Lançador do PCBoost.Server na Discloud.
# A Discloud não roda .NET nativamente; por isso o servidor vai compilado como executável Linux
# autocontido (com o .NET embutido) na pasta server/, e este script Python apenas o inicia.
# Gere a pasta server/ e o .zip com:  scripts/deploy-discloud.ps1
import os
import stat
import sys

here = os.path.dirname(os.path.abspath(__file__))
binary = os.path.join(here, "server", "PCBoost.Server")
if not os.path.isfile(binary):
    sys.exit("server/PCBoost.Server não encontrado. Gere o pacote com scripts/deploy-discloud.ps1.")

# O .zip criado no Windows não guarda a permissão de execução.
os.chmod(binary, os.stat(binary).st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

env = dict(os.environ)
env["PORT"] = "8080"  # sites na Discloud precisam escutar em 0.0.0.0:8080
env.setdefault("PCBOOST_DATA", os.path.join(here, "data", "pcboost-team.db"))
env.setdefault("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "1")
env.setdefault("ASPNETCORE_ENVIRONMENT", "Production")
# Site de download (/): o .exe (~70 MB) não cabe no limite de 100 MB do .zip junto com o servidor,
# então o botão "Baixar" redireciona para o anexo da última release no GitHub
# (gerada por scripts/publish-release.ps1). Um arquivo em downloads/PCBoost-Setup.exe tem prioridade.
env.setdefault("PCBOOST_DOWNLOADS", os.path.join(here, "downloads"))
env.setdefault("PCBOOST_DOWNLOAD_URL", "https://github.com/Raidoxxx/probable-app/releases/latest/download/PCBoost-Setup.exe")
print("PCBoost.Server iniciando na porta 8080; dados em", env["PCBOOST_DATA"], flush=True)
os.execve(binary, [binary], env)
