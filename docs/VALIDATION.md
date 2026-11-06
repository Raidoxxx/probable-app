# Validação do PCBoost

Execução em Windows neste PC em 06/11/2026. Stack preservada: WPF/.NET 8, sem pacotes adicionais. As capturas mostram o app com contadores reais do Windows.

## Resultados

| Verificação | Resultado e alcance |
| --- | --- |
| Build Release e publicação | Sem erros e sem avisos. Executável final em dist/PCBoost.exe. |
| Interface | Seis seções na mesma janela, dashboard, gráfico, barra compacta, regras, editor de projeto, log e configurações. Contadores opcionais ausentes continuam como indisponíveis. |
| Memória nativa | Memória física, disponível, comprometida/limite e pools consultados no Windows. Standby e hardware reservado permanecem nulos nesta fonte. |
| Histórico e alertas | Persistência, expiração, frequência separada de processos, baixa disponibilidade sustentada, comprometimento alto, crescimento de pool e lacunas que não contam como pressão contínua. |
| Identidade e perfis | Preservar por padrão, autoproteção e seleção vazia sem fechar aplicativos. Regressões também verificam identidade obsoleta, processos protegidos e persistência anterior. |
| Execução nativa | Filho suspenso associado a job próprio antes de executar; força recusada sem tentativa normal. Wrapper cmd com argumento estruturado e rejeição de metacaracteres. |
| Maven real | Maven 3.9.9, package e test offline, compilação javac, JUnit 5.12.2, relatório recente confirmado. |
| Gradle real | Distribuição Gradle 9.8.2 em cache, wrapper de fixture detectado, jar e test offline, JUnit com XML confirmado. Cache de trabalho da fixture dentro da pasta de verificação. |
| Pipeline | JDK real Amazon Corretto 25.0.3; build/testes sequenciais, escolha automática de JAR único, instalação com backup e servidor Java controlado com sinais separados de prontidão/plugin. |
| Falhas e concorrência | Memória baixa simulada no preflight; porta TCP ocupada de verdade; lock simultâneo do mesmo ambiente; destino somente leitura conserva plugin; erro real de sintaxe Java impede instalação; relatório antigo e relatório com todos os testes ignorados não confirmam execução. |
| Diagnóstico | Exemplos de logs de heap, Java incompatível, porta, compilação, testes e causa desconhecida. Classificação textual testada; não foi provocada falta de memória real neste PC. |
| Cancelamento | Parada normal do simulador via stdin; cancelamento de build controlado com tentativa normal antes da força; job sentinela independente continua ativo. |
| Regressões antigas | Testes de proteção, identidade, lista, memória e limpeza em fixtures isoladas continuam passando. Esses testes não significam que o novo painel execute limpeza ou trim. |

### Consumo em repouso

Amostra de 30 segundos, dashboard aberto, métricas a cada 5 s e processos a cada 20 s, após aquecimento de 5 s:

- Working set do próprio app: **126,9 MB**.
- Memória privada comprometida: **78,9 MB**.
- CPU: **4,69% de um núcleo**, equivalente a **0,391% da capacidade total** deste PC.

É uma amostra neste ambiente, não um limite garantido. O teste não altera aplicativos externos. A tabela só é reconstruída quando chega nova leitura de processos, e o indicador animado fica desligado em repouso.

O PCBoost.dll publicado em dist é idêntico ao binário testado em dist-next (SHA-256: 9AFA87B6AEC04AEED024F2E82EDF89BA0376269D2F8558509157DAB44B014272).

## Evidências

Relatórios na raiz: self-test-result.txt, ui-test-result.txt, integration-test-result.txt e idle-test-result.txt. Capturas: ui-preview.png, ui-compact-preview.png, diagnosis-preview.png, games-preview.png e development-preview.png.

As integrações criam pastas datadas em dist-next/verification durante a implementação; novas execuções pelo binário final criam dist/verification. Cada pasta contém projeto Java artificial, servidor artificial, estado isolado, logs e artefatos de build. integration-test-result.txt identifica a pasta exata da execução mais recente. Nenhum jogo, IDE ou servidor existente do usuário foi encerrado para validar o fluxo de perfis.

## O que ainda exige projeto real

**FixtureServer não é Paper nem PowerNukkitX.** Ele abre uma porta local, confere os metadados do JAR instalado, imprime marcadores configurados e responde a stop. A prova cobre a orquestração de processos, build/testes e instalação; não comprova compatibilidade de um plugin Minecraft real, execução de eventos nem carregamento pelo motor real do servidor. O Gradle da fixture usa uma distribuição local através de um script gradlew.bat artificial; downloads e bootstrap de wrappers não foram testados.

Não foram testados um jogo/launcher real, confirmação de documento não salvo de um aplicativo externo, falta de memória física real, falhas de driver ou apresentação em uma instalação Windows EN-US. As APIs nativas não dependem dos nomes traduzidos de contadores; a execução foi neste Windows em PT-BR.

## Reproduzir

1. Na raiz do projeto, execute os comandos de build/publicação e os quatro argumentos de verificação indicados no README. Aguarde cada processo terminar e confira PASS / SKIP / erro nos relatórios. A integração usa as dependências já disponíveis localmente e não baixa servidores. Para Gradle fora do cache padrão, defina PCBOOST_TEST_GRADLE com o caminho de gradle.bat.
2. Para o seu plugin, prepare uma pasta dedicada com o servidor real e os arquivos exigidos por sua distribuição. Cadastre projeto, JDK e comandos, portas e margens. Configure padrões específicos para prontidão e confirmação do plugin, observando as mensagens reais da distribuição.
3. Rode **Checar ambiente**, revise os apps candidatos sem selecionar aplicativos com trabalho aberto e clique em **Testar plugin**. Confirme em separado build, teste, prontidão e plugin. Se o último padrão não estiver configurado ou não aparecer, o app deve continuar mostrando carregamento inconclusivo.
4. Inspecione o JAR e o backup no ambiente dedicado e faça um teste funcional do plugin dentro do servidor. Clique **Parar** e confira que a porta foi liberada. Somente se o processo continuar ativo, use a confirmação separada de parada forçada da própria sessão.
5. Repita com um erro de compilação no seu projeto e uma porta ocupada: nenhum plugin deve ser instalado após build com erro. Para pressão de memória, aumente temporariamente o limite mínimo no perfil em vez de esgotar a RAM do PC; o fluxo deve pedir revisão, nova checagem ou autorização explícita para continuar. Restaure o limite depois.
