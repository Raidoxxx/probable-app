# Correções da auditoria técnica — 6 de novembro de 2026

As alterações foram aplicadas ao código local, preservando o trabalho existente na interface. Não houve deploy nem publicação de release.

| Item da auditoria | Resultado |
| --- | --- |
| 1. Conexões após sair do servidor | Saída remove todas as conexões da conta dos grupos de mensagens e voz. Entrada, saída, conexão e chamadas protegidas são coordenadas pelo mesmo controle de concorrência. |
| 2. Áudio silenciado | Servidor consulta o estado de silenciamento antes de retransmitir; chamadas removidas não transmitem. |
| 3. Login e proxies | Cabeçalhos encaminhados ficam desativados sem proxies explicitamente cadastrados. Limites independentes de 10 tentativas/minuto por IP e conta normalizada. |
| 4. Revogação de sessão | Conexões são associadas ao hash da sessão. Logout aborta todos os sockets do token; cada chamada revalida a sessão. Sessões ociosas expiradas são encerradas na varredura de 5 segundos. |
| 5. Transporte | Cliente exige HTTPS para hosts remotos, aceita HTTP em localhost e IPs de loopback e impede redirecionamento HTTP automático na autenticação. |
| 6. Privacidade de membros | DTO público não contém e-mail; tooltip usa a função do membro. |
| 7. Fila de áudio | Cinco quadros pendentes, um consumidor e descarte dos mais antigos. Falhas e timeout encerram o envio; silenciar invalida quadros acumulados. |
| 8. Abuso de áudio | PCM precisa ter tamanho par e até 8192 bytes. Limites por conexão: 35 pacotes e 40000 bytes por segundo. Cem descartes por excesso dentro da mesma janela abortam a conexão. |
| 9. Evolução da voz | Falhas de captura, reprodução e rede encerram a chamada com mensagem. Opus, jitter adaptativo, volume individual, redução de ruído e seleção/troca de dispositivos continuam como evolução arquitetural; não foram implementados nesta correção. PCM permanece compatível. |
| 10. Prioridade original | Jogo é definido antes de examinar processos de fundo. Um único registro preserva a primeira prioridade de cada identidade de processo. |
| 11. Recuperação após falha | Journal registra PID, início UTC exato, caminho e prioridade, antes da alteração. Escrita com flush e substituição do arquivo. Reinício revalida a identidade e restaura processos e energia. Falhas de restauração mantêm o registro para nova tentativa. |
| 12. Fechamento durante ativação | Estados Idle, Activating, Active e Restoring, cancelamento e bloqueio coordenam as mutações. Fechamento sempre pede restauração, inclusive durante ativação. |
| 13. Alegação de desempenho | Removidos o esvaziamento automático de working sets e o suposto ganho de RAM do resumo. Exibidas memória disponível e comprometida. Não há alegação nem medição de ganho de FPS, 1% low, faltas de página ou latência nesta entrega. |
| 14. Executáveis auxiliares | Caminho explícito do jogo tem preferência; sem ele, seleção usa processo em primeiro plano com janela dentro da pasta correta. Helpers, launchers, instaladores e relatórios de falhas são excluídos. |
| 15. Atualizações | Manifesto RSA-PSS/SHA-256 assinado, hash do instalador, WinVerifyTrust/Authenticode e certificado fixado no build. Verificação repetida antes da execução com arquivo bloqueado contra escrita. Releases sem assinatura são recusadas. |
| 16. Backup e tokens | Backup pela API online do SQLite, integridade, referências, ensaio de restauração em outro banco e retenção. Tokens passam a ser hashes SHA-256; tokens antigos são migrados sem trocar os valores dos clientes. Destino externo depende da configuração da hospedagem. |
| 17. Runtime | Desktop, servidor, contratos, Docker e workflows migrados para .NET 10; SignalR e Microsoft.Data.Sqlite em 10.0.14. |

## Configuração do servidor

Variáveis lidas também pelo sistema de configuração do ASP.NET Core:

| Variável | Comportamento |
| --- | --- |
| `PCBOOST_KNOWN_PROXIES` | IPs exatos separados por vírgula. Vazio: ignora `X-Forwarded-For` e `X-Forwarded-Proto`. Informar os IPs reais do proxy da hospedagem; não cadastrar clientes nem curingas. Sem isso, usuários atrás do mesmo proxy compartilham o limite de IP. |
| `PCBOOST_BACKUP_DIR` | Pasta de destino. Padrão: `backups` ao lado do banco. Para proteção fora da hospedagem, montar armazenamento externo persistente nessa pasta ou replicar os snapshots verificados para um serviço externo. Um backup local não sobrevive à perda da hospedagem. |
| `PCBOOST_BACKUP_HOURS` | Intervalo entre backups, de 1 a 168 horas; padrão 6. O primeiro ocorre na inicialização. |
| `PCBOOST_BACKUP_KEEP` | Quantidade mantida, de 1 a 1000; padrão 28. Retenção só roda após criar e verificar um backup novo. |

Todo snapshot é restaurado em um banco temporário separado em disco e suas tabelas são consultadas. Isso evita carregar o banco inteiro na RAM do servidor. Para verificar novamente um arquivo:

```sh
dotnet PCBoost.Server.dll --verify-backup /caminho/pcboost-backup.db
# Ou o pacote autocontido:
./PCBoost.Server --verify-backup /caminho/pcboost-backup.db
```

Para restauração operacional, parar o servidor, preservar o banco atual e seus arquivos WAL/SHM, verificar o snapshot e iniciar uma instância separada com `PCBOOST_DATA` apontando para uma cópia dele. Conferir contas, servidores e mensagens antes de substituir os dados de produção. Não misturar um snapshot restaurado com arquivos WAL/SHM de outro banco.

## Releases assinadas

O workflow Release e o script `scripts/publish-release.ps1` exigem:

- `UPDATE_CERTIFICATE_PFX`: certificado Authenticode válido com chave privada, em PFX/base64.
- `UPDATE_CERTIFICATE_PASSWORD`: senha do PFX.
- `UPDATE_MANIFEST_PRIVATE_KEY`: chave RSA privada em PEM, mantida estável entre releases. Recomenda-se gerar uma chave de 3072 bits para esse uso.

Configurar esses valores como secrets do repositório. O script requer PowerShell 7.4 ou superior, assina com timestamp, fixa o hash SHA-256 do certificado e a chave pública do manifesto no executável e gera os três anexos: `Ximbuismo-Setup.exe`, `update-manifest.json` e `update-manifest.sig`.

```powershell
pwsh -File scripts/publish-release.ps1
```

O build de desenvolvimento via `build-installer.ps1` continua possível sem secrets; esse build recusa a instalação automática de atualizações porque não tem publicador/chave pública configurados. Releases antigas sem manifesto assinado também são recusadas. Rotação de certificado ou chave precisa de uma estratégia de transição para os clientes já instalados.

Não foram criados certificado, chave privada ou armazenamento externo. A assinatura real e o fluxo até a execução de um instalador de produção dependem desses recursos.

## Validação reproduzível

Requer SDK .NET 10 em Windows:

```powershell
dotnet build tests/SecurityRegression/SecurityRegression.csproj -c Release
dotnet tests/SecurityRegression/bin/Release/net10.0-windows/SecurityRegression.dll (Get-Command dotnet).Source apps/server/bin/Release/net10.0/PCBoost.Server.dll
```

A suíte inicia um servidor SQLite descartável e clientes SignalR reais. Cobre duas conexões da mesma conta, mensagens e áudio após saída, silenciamento, pacotes inválidos, limites de tráfego, revogação ativa, expiração ociosa, desconexão de chamadas, migração e armazenamento de tokens, backup/restauração com conteúdo recente, spoofing de IP, HTTPS, fila limitada e seus erros, assinatura e adulteração de manifesto e restauração de prioridades com processos filhos isolados. O cancelamento do Boost é exercitado antes de qualquer alteração no computador. Não é executado Boost sobre os aplicativos reais do usuário.

O workflow `security-regression.yml` repete a suíte em Windows e publica/inicia o pacote Linux em um runner Ubuntu, verificando também a restauração de backup.

No ambiente local: compilação .NET 10 sem erros/avisos e geração de pacotes autocontidos `win-x64` e `linux-x64`. O pacote Linux é compilado aqui; sua execução depende do runner Linux. Microfones físicos, troca de dispositivos, FPS e assinatura de produção não são cobertos por esse teste.
