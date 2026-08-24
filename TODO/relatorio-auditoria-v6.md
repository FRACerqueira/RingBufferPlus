# Relatório de auditoria pré-release — v6.0.0

Documento de achados point-in-time (não histórico de execução — isso fica em
`plano-de-acao-v6.md`). Atualizado em cada round, achados fechados marcados com ✅
em vez de removidos.

**Escopo:** branch `v6`, pós ADR001V03 (modelo de concorrência) e ADR007V03
(superfície pública), ambos completos em `d9b55ad`. Não reaproveita achados do
`relatorio-viabilidade-ringbufferplus-v5.md` — aquele documento audita o motor v5.1
(`develop`), que o v6 substituiu por um redesenho from-scratch; a maioria dos
achados lá não se aplica a código que não existe mais aqui.

**Ângulos confirmados (2026-08-23):**
1. `auditoria-estabilidade` — concorrência/correção
2. `auditoria-resiliencia` — falha/shutdown/degradado
3. `auditoria-usabilidade` — superfície pública/docs
4. `auditoria-complexidade` — algorítmico/alocação
5. `auditoria-desempenho` — empírico (benchmarks antes/depois)
6. `auditoria-observabilidade` — telemetria (métricas/logs/traces)

---

## Apoio: grafo de conhecimento (graphify)

`graphify-out/` atualizado (`graphify --update`) para o estado atual de `v6`,
pós ADR001V03/ADR007V03. 1015 nós, 2140 arestas, 79 comunidades. Health check OK
(0 arestas penduradas/faltantes, 0 self-loops, 0 colapsos). Diff vs. grafo anterior:
+684 nós, +1782 arestas — reflete a magnitude do redesenho.

God Nodes: `RingBufferContractTests` (85 arestas), `RingBufferManager` (57),
`RingBufferBuilder` (36), `RingBufferBuilderTests` (32), `ADR007V03` (28).
`RingBufferContractTests` age como ponte entre testes/ADRs/núcleo de autoscale —
apontado como bom ponto de partida para a frente de estabilidade.

Conexões inesperadas (INFERIDAS, não confirmadas — cada frente deve verificar contra
o código antes de tratar como achado): guia `usage-fixed-capacity` ↔ `ElasticCapacity`;
guia RabbitMQ ↔ `Factory`; workflow de publish ↔ `CHANGELOG`.

Usado como mapa de apoio pelas 6 frentes abaixo (não como fonte de verdade — o
código é a fonte de verdade).

## Round 1 — 2026-08-23 (reiniciado com apoio do grafo)

A primeira tentativa do Round 1 foi cancelada a pedido do usuário antes de concluir,
para reiniciar já com o grafo atualizado disponível. Nenhum achado daquela tentativa
foi preservado.

Status: em andamento.

### auditoria-estabilidade — concluída (retentativa)

- **[MÉDIO — CONFIRMADO 2026-08-24, 2/2 instâncias independentes concordam]** O pin
  (`_pinExpiresAt`) do `SwitchToAsync` é delimitado por tempo, não por eficácia: é
  setado (`RingBufferManager.cs:773`) assim que um batch é DESPACHADO, não quando ele
  termina de fato de atingir o alvo. `DispatchScaleDown` (`1099-1109`) faz `TryRead`
  oportunista — pode remover menos itens que o pedido se o pool estiver saturado
  (itens em uso). Enquanto o pin não expira, o Monitor (único sinal que reavaliaria e
  tentaria completar a redução) fica suprimido (`813-828`). Resultado: um
  `SwitchToAsync(MinCapacity, pinDuration)` pode ficar "preso" numa capacidade
  intermediária pelo tempo inteiro do pin, mesmo que os itens retidos voltem a ficar
  idle segundos depois — e o caminho sem `LockWhenScaling` já retornou `true` ao
  chamador, que não tem visibilidade de que o resultado foi parcial. Cenário
  irmão via backlog-reactive: backlog escala para cima durante um pin (comportamento
  correto, por design), mas quando o backlog esvazia a capacidade fica presa no valor
  elevado (não no valor originalmente pinado) até o pin expirar. Ponto cego de
  cobertura identificado: `ScaleDown_WithNotEnoughIdleItems_DoesNotBlockTheEngineForOtherCommands`
  (`RingBufferContractTests.cs:1358-1394`) monta esse cenário mas só verifica que outro
  comando não fica bloqueado — nunca afirma a capacidade final.

  **Corroboração (2ª instância independente, 2026-08-24): CONFIRMA.** Raciocínio-chave:
  a assimetria estrutural é real — para scale-up, uma falta parcial se autocorrige
  porque `EvaluateBacklogReactive`/`EvaluateFloorGuard` são reavaliados a cada
  `FactoryBatchCompleted` e nunca são gateados pelo pin; para scale-down não existe
  equivalente, só o Monitor (suprimido durante o pin) tentaria terminar a redução.
  A 2ª instância inicialmente se inclinou a REFUTAR lendo só a doc pública (achando
  que "partial result is not undone" implicava "nunca mais tentado", uma decisão
  deliberada) — o advisor apontou a confusão e, ao checar ADR001V03/ADR010V01
  diretamente, confirmou que NENHUMA ADR decide ou nomeia esse caso específico
  (ADR010V01 é só sobre scale-up). Não é trade-off documentado, é lacuna real.
  Severidade Médio confirmada por ambas: sem corrupção de estado/overshoot de bounds,
  autocorrige quando o pin expira, mas `pinDuration` não tem teto e o caminho default
  (`LockWhenScaling=false`) não dá visibilidade nenhuma do resultado parcial ao
  chamador.

  ✅ **Decisão do usuário (2026-08-24): aceitar como comportamento conhecido, só
  visibilidade — sem retry.** Opção descartada: retry real no turnback (adiciona
  `_pinTarget` + gancho em `TurnbackAsync`) — mais arriscado, tocaria o mesmo hot
  path do fix do hang de `DisposeAsync` desta sessão. Implementado: `LogWarning` em
  `RemovalBatchCompleted`'s handling quando `!scaledDown && scaleTrigger == "manual"`
  (não relevante para scale-down `"auto"`, que já se reavalia a cada tick). Guia
  `usage-elastic-manual-scale.md` documentado com o trade-off completo. Red/green
  feito (`ScaleDown_PartialUnderActivePin_LogsWarning`).
- ✅ **[BAIXO — corrigido 2026-08-24]** `_disposed` (`RingBufferManager.cs:102`) é `bool` não-volátil lido
  entre threads sem barreira de memória explícita — teoricamente uma leitura obsoleta
  é possível, mas nenhum cenário de corrupção concreto foi encontrado (a escrita é a
  primeira instrução síncrona de `DisposeAsync`, e o `CancellationTokenSource`
  subsequente já fornece sincronização adicional nos caminhos que importam).
  Observação de robustez teórica, não bug demonstrado.
- Sem achado (verificado e correto): decremento de `_waitingCount`, concorrência no
  `Channel<T>` entre `DispatchScaleDown` e `AcquireCoreAsync`, aritmética de
  `_currentCapacity` (sempre serializada na thread do engine), gate único `_scaling`,
  corrida teórica e inofensiva entre comando pós-batch e shutdown, retry de warmup
  (ADR011) vs. `DisposeAsync`, ausência de campos estáticos mutáveis,
  `RingBufferValue<T>.DisposeAsync()` idempotente via `Interlocked.Exchange`.
- Nota incidental (sem ação): detectou o worktree órfão `.claude/worktrees/agent-a348fb725d5d12d2b`
  já conhecido — confirmou não ter usado suas conclusões.

### auditoria-resiliencia — concluída (retentativa)

- ✅ **[ALTO, com argumento para Crítico — corrigido 2026-08-24]** `DisposeAsync` pode nunca retornar, e seu
  `finally` de limpeza (drenar `_availableItems`, dispose de `_lifetime`/`_meter`/
  `_activitySource`) nunca roda — buffer fica **permanentemente** indisponível para
  dispose (`_disposeGuard` torna uma segunda chamada um no-op silencioso).
  Mecanismo: `TurnbackAsync`'s branch `SkipTurnback` (`RingBufferManager.cs:698-699`)
  chama `DisposeItemAsync` cru — sem `Task.Run`/`WaitAsync(PulseHeartBeat)`, ao
  contrário de todo o resto do código. Se um `HeartBeat` retornar `false` (item
  saudável dentro do orçamento do pulso, não o ramo de timeout) e o `Dispose()`/
  `DisposeAsync()` do item travar (ex.: `Close()` de uma conexão TCP morta bloqueando),
  `RunHeartbeatAsync` nunca retorna → `_heartbeatTask` nunca completa →
  `DisposeAsync` trava em `await Task.WhenAll(pending)` (`:508`), **dentro do `try`**.
  Não é elastic-específico — reproduzido com `FixedCapacity(2)`. **Confirmado
  empiricamente** com um probe descartável (`HangingDisposeProbe` já existente na
  suíte): `DisposeAsync` não retornou em 2s (probe removido após uso, árvore limpa).
  Escopo do fix: `TurnbackAsync` chama `DisposeItemAsync` cru em 2 pontos — linha 699
  (o que causa o travamento de shutdown) e linha 706 (branch `ChannelClosedException`,
  impacto local ao caller, não ao shutdown).
- ✅ **[ALTO — corrigido 2026-08-24]** `AddRingBuffer<T>` acopla falhas entre buffers não relacionados do mesmo
  `T`: `HostingExtensions.cs:58` usa `GetServices<IRingBufferService<T>>()`, que força
  a construção de **todos** os buffers registrados daquele `T`, não só o nomeado. Um
  erro de configuração em um buffer "B" (ex.: `min > max`) faz o `StartAsync` do
  hosted service de um buffer "A" completamente saudável lançar — e como o Generic
  Host aborta o startup inteiro quando qualquer `IHostedService.StartAsync` lança, um
  typo de config em um buffer pode impedir o host inteiro de subir. **Confirmado
  empiricamente** com um probe descartável (removido após uso). Não é vazamento de
  recursos (DI container faz dispose de tudo corretamente) — o dano é acoplamento de
  falha + atribuição errada de causa. Dois efeitos colaterais menores da mesma causa:
  (a) o engine loop de "B" começa a rodar durante o `StartAsync` de "A", antes do
  hosted service de "B" ser chamado; (b) dois `AddRingBuffer<T>` com o mesmo
  `buffername` fazem `FirstOrDefault` sempre resolver a mesma instância — a segunda
  nunca recebe o warmup automático prometido pelo ADR007V03 (só aquece sob demanda via
  `EnsureWarmupAsync`, contradizendo silenciosamente "warmup agora é automático").
  Nota de direção (não é do escopo desta frente corrigir): `AddKeyedSingleton` já
  disponível nas 3 TFMs referenciadas, viável para desacoplar por `buffername`.
- ✅ **[MÉDIO — corrigido 2026-08-24]** `PulseHeartBeat` sustenta 3 bounds de disposal diferentes no v6
  (`DisposeOneItemDefensivelyAsync`, grace period de `_pendingHeartbeatDisposals`,
  timeout do próprio pulso), mas `ValidateBuild` não valida um piso para ele (ao
  contrário de `SamplesBase/SampleUnit`, que já tem piso de 100ms). Um
  `TimeSpan.Zero` explícito (erro plausível de unidade) passa sem erro e faz todo
  dispose defensivo expirar instantaneamente, tratando disposes normais como
  travados. `FactoryTimeout`/`AcquireTimeout` têm a mesma lacuna, mas são
  pré-existentes (fora de escopo do v6, não reportado como item novo).
- Negativo confirmado (era a suspeita inicial do prompt, descartada por rastreamento):
  `_factoryBatchTask` e `_removalBatchTask` SÃO limitados corretamente (linked a
  `_lifetime`, timeouts por item, `DisposeItemsDefensivelyAsync` já usado) — o
  Achado 1 (`_heartbeatTask`) é o único wait genuinamente sem limite em `DisposeAsync`.
- Sem achado além do já aceito: exceção em callback de heartbeat não invalida o item;
  pin do `SwitchToAsync` nunca suprime floor guard/backlog-reactive; trade-off "4x
  amplification" não reaberto.

### auditoria-usabilidade — concluída (retentativa)

- ✅ **[CRÍTICO — corrigido 2026-08-24]** `src/RingBufferPlus/README.txt:142-144` (README embutido no pacote NuGet)
  afirma que um scale-up parcial é "desfeito" (`undone`) no timeout de `SwitchToAsync`
  com `LockWhenScaling()`. Isso contradiz diretamente `ADR010V01` (decisão explícita:
  manter progresso parcial, nunca desfazer), `IRingBufferManualScaleService.cs:47`
  ("A partial result is **not** undone...") e o guia irmão
  `doc/guides/usage-lock-when-scaling.md:33/37`, que documentam o comportamento correto.
  Está na superfície mais visível do pacote (README do NuGet).
- ✅ **[ALTO — corrigido 2026-08-24]** `doc/guides/usage-rabbitmq.md:44` afirma que só `Fault` dispara scale-up
  e que `Tick` "nunca" dispara (`"never calls Factory at all"`) — falso: `ProcessTick`
  chama `DispatchScaleUp` sempre que `target > CurrentCapacity`
  (`RingBufferManager.cs:1241-1244`), contradizendo o guia irmão
  `usage-elastic-autoscale.md:30`. Também cita o trigger `"Fault"`, que não existe mais
  como `EngineCommandKind` (removido no backlog-reactive work) — terminologia
  pré-ADR001V03 vazou para um guia reescrito nesta versão.
- ✅ **[ALTO — corrigido 2026-08-24]** `doc/architecture/overview.md:21,38,44` descreve `AutoScaleDecision`
  (`Median`/`EvaluateScaleDown`) como componente atual, com caminho de arquivo que não
  existe mais (`AutoScaleDecision.cs` foi deletado, substituído por
  `AutoScaleMonitor.cs`/`FloorGuardDecision.cs`) e referencia `ADR003V02`/`ADR001V02`
  (supersedidas) em vez de V03. É o documento que `CONTRIBUTING.md`/`README.md`
  apontam como mapa de entrada para novos contribuidores.
- ✅ **[MÉDIO — corrigido 2026-08-24]** `doc/guides/usage-observability.md:56` linka para
  `doc/guides/usage-background-logger.md`, que não existe — `BackgroundLogger` foi
  removido inteiramente por ADR007V03. Link morto para feature removida.
- ✅ **[BAIXO — corrigido 2026-08-24]** `doc/guides/concepts.md:18` cita `ADR001V02`/`ADR003V02` (supersedidas)
  para o conceito central de elastic capacity, em vez de V03. Na mesma varredura,
  encontradas e corrigidas 2 ocorrências NOVAS da mesma classe (não pegas por nenhuma
  das 6 frentes): `usage-observability.md:35` citava `ADR001V02`; `ADR007V03` (já
  Accepted) afirmava "`LockWhenScaling` stays removed", o que é falso — o método está
  presente e documentado no código atual (confirmado via `git log -S`: removido em
  `a3a8222` na era v5.1.0, reintroduzido antes da formalização do ADR007V03). Usuário
  confirmou tratar como correção factual pontual no próprio ADR, não uma nova decisão.
- Sem achado: todas as interfaces públicas (XML docs), `HostingExtensions`,
  fórmula do backlog-reactive no guia vs. código, `RingBufferDefault.cs` vs. guias,
  os 5 samples (todos usam a API nova corretamente), docs de API gerados (limpos),
  `CONTRIBUTING.md`.
- As 2 conexões "surpreendentes" apontadas pelo grafo (`usage-fixed-capacity` ↔
  `ElasticCapacity`, `usage-rabbitmq` ↔ `Factory`) foram checadas e são falsos
  positivos de similaridade semântica — sem confusão de escopo real.

### auditoria-complexidade — concluída

Passe estática, sem medição — todos os itens abaixo são hipóteses que dependem de
confirmação empírica pela frente de desempenho antes de virar decisão de refatoração.

- **H1 (Baixa, pode subir p/ Média) — descartado, decisão do usuário 2026-08-24:**
  refatorar sem evidência empírica de que importa nos defaults seria complexidade
  especulativa; não muda a classe de complexidade geral (o `OrderBy` de H2 já domina
  o custo por tick de qualquer forma), só um fator constante. Sem ação.
  `RingBufferManager.cs:1214-1218` (`ProcessTick`):
  janela deslizante `_samples` é `List<int>` com `RemoveAt(0)` — O(n) por tick (Array.Copy),
  em vez de um array circular O(1). Nos defaults (`SamplesCount=100`, tick ~300ms)
  irrelevante; só relevante se `numberSamples` for configurado muito alto (o piso de
  100ms em `baseTimer/numberSamples` limita a taxa de ticks a 10/s, o que autolimita
  o pior caso).
- **H2 (Baixa) — descartado, mesma decisão acima.**
  `AutoScaleMonitor.cs:47` (`Percentile`): `samples.OrderBy(x=>x).ToArray()`
  — O(n log n) + alocação por tick. Não pode compartilhar a cópia ordenada com `Slope`
  (que precisa da ordem temporal original). Mesma escala/ressalva de H1.
- ✅ **H3 — FECHADO 2026-08-24: severidade Baixa, decidido por desempate 2/3, sem fix.**
  `RingBufferManager.cs:1439-1444` (`CreateItemsAsync`): fan-out de `quantity` `Task`s
  de uma vez, mesmo com `MaxConcurrentFactoryCalls` (default 4) limitando execução
  concorrente real via semáforo. 1ª instância: Média candidata (custo de setup ainda
  é O(quantity), sem guard-rail). 2ª e 3ª instâncias (voto de desempate): Baixa —
  overhead de constante numa operação já O(n) em qualquer implementação, dominado
  por várias ordens de grandeza pelo custo real do `Factory` (ms-s, conexão de
  rede/DB, vs. nanosegundos de setup); único caminho de exposição a `quantity` grande
  é o warmup (`MoveToCapacityAsync`, único chamador restante, roda uma vez no ciclo
  de vida), scale-up incremental usa apenas o delta gateado por amostragem (piso
  100ms). A 3ª instância confirmou por leitura própria dos defaults do projeto
  (`Capacity=2`, `FactoryTimeout=15s`) que o dimensionamento do projeto já assume essa
  proporção. Achado separado, roteado para fora do pilar de complexidade (validação/
  robustez, não algoritmo): `ValidateBuild` não impõe teto superior em
  `_initcapacity`/`_maxCapacity`.

  ✅ **Decisão do usuário (2026-08-24): sem teto, mantém como está.** Ao contrário do
  piso do `PulseHeartBeat` (fix desta sessão — zero/negativo quebra uma invariante
  técnica clara), não existe um teto tecnicamente correto para `MaxCapacity`/
  `_initcapacity`: são inteiramente dependentes do domínio (pool de conexões
  DB/canais RabbitMQ/etc.), qualquer número seria arbitrário sem justificativa
  técnica, e poderia quebrar um uso legítimo de escala grande. Sem incidente
  relatado, e as 3 instâncias do achado H3 já confirmaram que o custo real é
  desprezível mesmo para `quantity` grande. Não implementado — decisão final, não
  pendência.
- ✅ **RESSALVA VERIFICADA 2026-08-24, lendo o código-fonte real do runtime
  (`dotnet/runtime`, `UnboundedChannel.cs` + `ConcurrentQueue.cs`).** `_availableItems.Reader.Count`
  (`Channel<T>` ilimitado, lido em `EvaluateBacklogReactive`/`ProcessTick`) delega
  para `ConcurrentQueue<T>.Count`, que **não é O(1) estrito** — é O(número de
  segmentos), via aritmética de índices head/tail por segmento (nunca enumera itens
  individuais; cada segmento comporta até 1024 itens antes de crescer para o
  próximo). Não muda a conclusão prática: `_availableItems` é limitado por
  `MaxCapacity` (pool de recursos, realisticamente dezenas a poucos milhares), então
  o número de segmentos envolvidos é sempre pequeno — o custo continua desprezível.
  **Severidade H3-adjacente confirmada como NÃO subindo para Alto** — a imprecisão
  era só na caracterização ("O(1)" vs. "O(segmentos), desprezível na prática"), não
  no impacto real.
- Descartado do escopo deste pilar (registrado, não é achado): boxing de tags em
  `_acquireDuration.Record`/`_acquireFaults.Add` (`RingBufferManager.cs:334-336,
  359-363, 378-382`) — exigido pelo próprio contrato de `System.Diagnostics.Metrics`,
  não uma escolha evitável de estrutura de dados. Relevante para observabilidade/
  desempenho, não para complexidade.
- Verificado e sem achado: `EvaluateBacklogReactive`, `FloorGuardDecision.*`,
  `DispatchScaleDown`'s dequeue, cálculo de `demand`/`active` em `ProcessTick` — todos O(1)
  ou O(quantity) sem realocação, adequados.
- Nota fora de escopo do agente: pediu para registrar uma pendência sobre um
  `model-tiering-notes.md` que não existe neste repo — parece instrução genérica do
  subagente não aplicável aqui, não uma pendência real deste audit. Sem ação tomada.

### auditoria-desempenho — concluída (retentativa)

**Nota operacional (não é achado de código):** este agente rodou `git worktree remove
--force` em dois worktrees órfãos (`C:/Sources/RingBufferPlus-baseline`/`-head`) que
presumiu serem lixo da tentativa anterior desta mesma frente (a que travou 600s em
`AcquireThroughputBenchmarks`). Não há garantia de que não pertenciam a outra coisa —
sinalizado ao usuário para ciência, não confirmado como seguro por mim.

- **[Ganho confirmado, não é achado de regressão]** Backlog-reactive reage
  estruturalmente mais rápido que o antigo fault-count: elimina por completo a espera
  do `AcquireTimeout` (antes ~50ms fixos do próprio timeout configurado no benchmark)
  e a execução do scale-up em si também ficou ~1.9x mais rápida (~26ms → ~13.7ms para
  fechar o mesmo gap de 24 itens, plausivelmente pela concorrência limitada da
  Fábrica). Baseline reproduzido nesta sessão (76-77ms, bate com os 77.46ms da
  ADR001V03) e HEAD medido (13.7-13.8ms) em worktrees temporários descartáveis. O
  "5.5x" (76/13.7) NÃO deve ser citado como métrica de ganho — é artefato do
  `AcquireTimeout=50ms` que o benchmark antigo escolheu, não um multiplicador que
  generaliza.
- **[Baixo]** `SwitchToAsync` scale-up: sem diferença detectável (~108µs ambos os
  lados). Scale-down: HEAD ~13-15µs mais alto em média, mas dentro do StdDev
  observado (20-43µs) — não é possível afirmar regressão além do ruído; se real, é
  operacionalmente irrelevante (µs numa operação de escala de segundos-minutos).
  Explicação mecanística plausível: v6 adiciona 2 idas-e-voltas por canal
  (engine→Remoção→`RemovalBatchCompleted`) onde antes o dispose era inline. Alocação
  idêntica nos dois lados — sem regressão de alocação neste caminho.
- **[Sem achado]** Caminho de acquire com capacidade fixa: sem regressão (HEAD levemente
  mais rápido, dentro do ruído) — esperado, já que `FixedCapacity` desliga
  Monitor/backlog-reactive inteiramente.
- ✅ **[Gap de cobertura — 3 benchmarks novos escritos e verificados 2026-08-24]**
  `ElasticAcquireUnderBacklogBenchmarks.cs` (acquire sob pool elástico com backlog
  real sustentado, Monitor ativo — ~132µs/117KB por operação sob contenção, rodada
  curta de verificação), `MonitorTickCostBenchmarks.cs` (custo per-tick do Monitor
  nos defaults de produção, replicando o padrão exato Add/RemoveAt(0) do
  `ProcessTick` — **~20µs, ~1.86KB por tick, confirma numericamente que H1/H2 são
  irrelevantes nos defaults**, como as hipóteses já suspeitavam sem número), e
  `ScaleRejectionCostBenchmarks.cs` (custo de `SwitchToAsync` ser rejeitado com um
  batch em voo — ~127µs/928B). `MonitorTickCostBenchmarks` precisou de
  `InternalsVisibleTo("RingBufferPlus.Benchmarks")` novo em `RingBufferPlus.csproj`
  (mesma entrada já existente para `RingBufferPlus.Tests`) para chamar
  `AutoScaleMonitor` (internal) diretamente — não é superfície pública nova. Todos os
  3 rodados em modo curto (`--job short -i`, toolchain in-process por causa da
  colisão de nome de projeto com o worktree órfão já conhecida) para confirmar que
  funcionam de verdade, não só compilam; números acima são indicativos de uma rodada
  curta, não uma medição estatisticamente robusta de release (isso ficaria para quem
  rodar os benchmarks completos antes do lançamento).
  **Atualização (Round 7, desempenho — rodada completa, sem `--job short`):**
  `MonitorTickCostBenchmarks` 14.64µs±0.44 (1.86KB, alocação idêntica ao indicativo;
  tempo ~27% menor, provavelmente warmup insuficiente na rodada curta) — confirma
  H1/H2 com número real, não mais indicativo. `ScaleRejectionCostBenchmarks` 125.1µs
  ±3.90 (928B) — praticamente igual ao indicativo, sem achado novo.
  `ElasticAcquireUnderBacklogBenchmarks` 99.7-101.2µs (81-94KB, 2 rodadas
  independentes) — ~24-30% abaixo do indicativo em tempo e alocação; direção "o
  indicativo estava superestimando o custo", não o oposto, então não mascarou
  nenhuma regressão real. Nenhuma das 3 mudou de conclusão.
- Devolvido para outras frentes: a pergunta se `_availableItems.Reader.Count` em
  `UnboundedChannel<T>` é O(1) é de leitura de runtime/BCL, não de medição — fica em
  aberto para `auditoria-complexidade`. O efeito de `Fault` ser rejeitado durante um
  batch em voo (atraso na substituição de item defeituoso) é questão de corretude/
  latência de recuperação, não de desempenho puro — encaminhado para
  `auditoria-estabilidade`.

### auditoria-observabilidade — concluída (retentativa)

- ✅ **[MÉDIO — corrigido 2026-08-24]** Taxonomia de `trigger` documentada (`usage-observability.md`)
  estava obsoleta: prometia só `manual`/`auto`, mas o código emite 4 valores
  (`manual`/`floor`/`backlog`/`auto`, `RingBufferManager.cs:776,954,1001,1243/1247`) —
  confirmado pela própria suíte de testes. Doc atualizada com os 4 valores e o que
  cada um significa; `ADR008V01` (V01, escrito antes do backlog/floor existirem)
  deixado intacto como registro histórico correto para sua época. Achado correlato
  ✅ **[BAIXO — corrigido]**: `scale.duration` agora carrega a tag `trigger` também
  (antes só `scale.operations` carregava) — código + doc + teste novo.
- ✅ **[MÉDIO — corrigido 2026-08-24]** Logs em texto livre de scale (`"Starting ScaleUp N."` etc.,
  `RingBufferManager.cs:1024,1046,1116,1125`) nunca interpolam `scaleTrigger`, mesmo
  disponível em escopo — quem só usa `ILogger` (sem Meter/Activity listener, cenário
  explicitamente tratado como válido pelo próprio guia) não consegue diferenciar um
  switch manual de uma brecha de floor guard pelo stream de log. Corrigido nos 4
  pontos de `DispatchScaleUp`/`DispatchScaleDown`; os 2 pontos equivalentes em
  `MoveToCapacityAsync` (warmup) deliberadamente deixados sem trigger — warmup não é
  uma "operação de scale" no sentido que as métricas `scale.*` descrevem.
- ✅ **[MÉDIO — corrigido 2026-08-24]** Telemetria do Monitor (`ProcessTick`) não carregava nenhum dos parâmetros/
  entradas que geraram a decisão — nem sequer tinha um log de debug, ao contrário de
  `DispatchScaleUp`/`DispatchScaleDown`. `trigger="auto"` era o único vestígio
  observável; não dava para responder "por que o Monitor decidiu X" a partir de
  telemetria de produção. Decisão do usuário: opções 1 (tags em métricas/tracing
  existentes) + 2 (log de Debug por tick), sem métrica dedicada nova (opção 3,
  descartada). Implementado: tag `target` (universal, todas as 4 triggers) em
  `scale.operations`/`scale.duration`/Activity `RingBufferPlus.Scale`; log de Debug
  em `ProcessTick` cobrindo os 3 desfechos (target==current, suprimido por deadband,
  despachado) com `demand`/`target`/`current` — o `demand` (específico do Monitor)
  ficou só no log, não nas métricas, para não precisar de tags nulas condicionais nas
  chamadas de `Counter<T>.Add`/`Histogram<T>.Record`. Red/green completo (revert
  temporário via backup de arquivo, confirma falha nos 2 testes novos, restaura,
  confirma verde). Doc atualizada (`usage-observability.md`: tabelas de métricas,
  parágrafo de tracing, novo bullet sobre o log de Debug).
- ✅ **[BAIXO/MÉDIO, documentação — corrigido 2026-08-24]** `OnError` **substitui** (não soma) o log de erro do
  `Logger` quando configurado — comportamento pré-existente confirmado via
  `git log -S`, não uma regressão do v6 (checado antes de reportar, por disciplina
  própria do agente). Redação corrigida nas 3 interfaces (`IRingBufferBuilder`/
  `IRingBufferFixedBuilder`/`IRingBufferElasticBuilder` — as 3 tinham a mesma
  duplicata da frase enganosa) e no `ADR007V03`; docs de API regeneradas
  (`dotnet build src/RingBufferPlus` → `dotnet build src/XmlDocMarkdownGenerator` →
  gerar, nessa ordem, por causa da cópia stale do gerador). Nenhum teste cobria o
  cenário combinado — não adicionado (é achado de doc, não de comportamento).
- ✅ **[BAIXO — corrigido 2026-08-24]** Link quebrado `usage-observability.md:56` → `usage-background-logger.md`
  (arquivo removido, feature `BackgroundLogger` não existe mais) — **corrobora
  independentemente** o mesmo achado já reportado pela frente de usabilidade.
- **Investigado e refutado** (não é achado): suspeita de parent-chaining de `Activity`
  entre scales consecutivos via `Activity.Current`/AsyncLocal. Confirmado por prova
  empírica (2 `SwitchToAsync` sequenciais, activities vieram como raízes
  independentes) E por argumento estrutural (gate `_scaling` garante no máximo uma
  activity de Scale em voo por vez, sem caminho de burla).

---

## Round 8 — 2026-08-24

Objetivo: verificar se os fixes do Round 7 (commits `1c0d0fa`..`5316a99`) não
introduziram regressão, e achar o que passou batido nas sete primeiras
rodadas.

**Escopo**: usabilidade, complexidade, desempenho, observabilidade com
enquadramento completo (nenhuma bateu 2 rodadas consecutivas sem achado
ainda). **Estabilidade disparada com escopo pontual, não completo**: só
para revisar o candidato H-D roteado pelo complexidade no Round 7
(`_pendingHeartbeatDisposals` como `ConcurrentBag<Task>` sem afinidade de
thread real) antes de qualquer decisão de troca estrutural — não é um
retorno ao enquadramento completo dessa frente, que segue formalmente
convergida. Resiliência não disparada.

**Estabilidade (escopo pontual)**: veredito "seguro trocar `ConcurrentBag`
por `lock`+`List<Task>`" — nenhum risco de correção identificado, com 2
recomendações de implementação (lock também na leitura, não só na escrita;
`RemoveAll` em vez do padrão take/re-add) e um bônus (fecha um hazard latente
de perda de entrada sob exceção entre os loops de drenagem/reinserção do
padrão antigo). Implementado.

**Complexidade**: revalidou H1-H4/H-A/H-B/H-C por leitura fresca completa,
incluindo 2 arquivos de produção nunca lidos antes nesta série
(`RingBufferDefault.cs`, `RingBufferExtension.cs`) — sem mudança de
conclusão. **Achado novo [Baixo, não medido]**: o try/catch do Round 7 em
`AcquireCoreAsync` adicionou um `Stopwatch.GetTimestamp()` incondicional
antes do `try`, pago em toda chamada (não só na falha) — invisível ao
`MemoryDiagnoser` que esta série usa para validar esse método, já que é
custo só de tempo, sem alocação. Roteado para desempenho medir antes de
qualquer decisão. Um segundo candidato (padrão "drenar e reconstruir" de
`_pendingHeartbeatDisposals`, O(n) por inserção) foi considerado e
rebaixado a "registrado, sem ação" pela própria frente — taxa de chegada
limitada pela cadência do heartbeat torna o efeito irrelevante nos
defaults, mesmo enquadramento de descarte já usado para H1/H2.

**Desempenho**: mediu com rebuild limpo e A/B contra o commit anterior ao
Round 7 (`9f708d4`) que o `Stopwatch.GetTimestamp()` extra do candidato de
complexidade custa **+20 a +24ns (+7-8%)** no caminho comum de
`AcquireAsync` — real, medido, causa isolada por probe reversível (mover o
timestamp para dentro do `catch` volta ao baseline). Zero regressão de
alocação. Também mediu (inconclusivo por ruído de máquina, não por falha
de medição) o branch do gate de `_waitingCount` — por inspeção de código, a
regressão não é fisicamente esperável nessa escala para o caminho comum
(`countsTowardFaultBudget == true`). Nota de processo relevante: a
checagem "não regrediu" do próprio Round 7 comparou contra o número já
documentado (uma cifra pós-mudança, rotulada como ordem de grandeza) —
estruturalmente incapaz de detectar um delta de ~22ns; a doc está correta,
o método de comparação é que precisava de A/B contra o commit anterior.

**Observabilidade**: confirmou os 2 fixes do Round 7 conectados. **2
achados Alto + 1 achado Médio, todos decididos com o usuário:**
- **Alto**: `SwitchToAsync` nunca recebeu o fix do Round 7 (o relatório
  daquela rodada afirmou incorretamente que sim — o commit só tocou
  `AcquireCoreAsync`). Decisão: não estender o mecanismo de telemetria a
  `SwitchToAsync` agora (ele nunca teve sinal próprio por chamada, só o
  eventual `"RingBufferPlus.Scale"` de um dispatch bem-sucedido) —
  documentado como limitação conhecida, não corrigido em código.
- **Alto**: a linha nova de falha de warmup cacheada era idêntica, em
  tags, a um shutdown comum (`success=false`/`timed_out=false`/
  `cancelled=false` nos dois casos) — um consumidor só de métricas não
  conseguia distinguir "buffer permanentemente quebrado" de "shutdown
  benigno". Corrigido com a 4ª tag `acquire.warmup_failed`/`warmup_failed`
  (red/green, estendendo os 4 testes de desfecho de `AcquireAsync`
  existentes). Resolve também o achado equivalente de usabilidade (doc:
  invariante `Error ⇔ timed_out=true` quebrada pela Activity nova).
- **Médio, investigado e REFUTADO**: suspeita de que `SwitchToAsync`
  manual com `LockWhenScaling=false` nunca logava uma falha genuína de
  factory. Reprodução empírica mostrou 4 chamadas de `LogError`
  (`MaxConcurrentFactoryCalls`), uma por tentativa concorrente — o
  `catch` por tentativa dentro de `CreateItemsAsync` já loga
  incondicionalmente, antes de qualquer agregação em nível de batch. Uma
  1ª sonda (com factory síncrono) sugeriu falsamente que só 1 tentativa
  rodava — artefato da própria sonda, corrigido com um factory
  genuinamente assíncrono. Mantido como guarda de regressão permanente.

**Usabilidade**: confirmou as 4 correções do Round 7. **2 achados novos,
instância única, ambos correspondendo (por ângulos diferentes) aos achados
de observabilidade acima** — não tratados como achados separados adicionais
na consolidação, já que a mesma tag nova (`warmup_failed`) e a mesma
decisão sobre `SwitchToAsync` os resolvem. Reconfirmou (via `git blame`,
desta vez também de minha parte) que comentários "Round 7/8,
Resiliência/Estabilidade" já existentes no código são da série de
auditoria pré-v6 (2026-08-21), não vazamento desta série.

Verificado: 193/193 testes net10.0 (era 192, +1 novo), build limpo (0
warnings, 0 errors) em toda a solução (3 TFMs, samples, benchmarks,
gerador de docs).

Status: **fechado**.

---

## Round 7 — 2026-08-24

Objetivo: verificar se os fixes do Round 6 (commits `ed72f67`..`1c0d0fa`) não
introduziram regressão, e achar o que passou batido nas seis primeiras rodadas.

**Escopo reduzido, por decisão do usuário**: estabilidade e resiliência
atingiram convergência formal no Round 6 (2 rodadas consecutivas sem achado
confirmado cada) e **não são disparadas nesta rodada** — não apenas
enquadramento mais curto como no Round 6, mas ausência completa desta vez,
já que o critério de convergência por frente que este documento define foi
satisfeito. As outras 4 frentes (usabilidade, complexidade, desempenho,
observabilidade) mantêm o enquadramento completo, já que nenhuma delas bateu
2 rodadas consecutivas sem achado ainda.

**Complexidade**: confirmou o escopo do Round 6 e revalidou H1-H4/H-A/H-B/H-C
por leitura fresca completa (incluindo 2 arquivos de produção nunca lidos
antes nesta série, `RingBufferDefault.cs`/`RingBufferExtension.cs`) — nenhuma
mudança de conclusão. Nota de precisão sem ação: `RingBufferDefault.Capacity=2`
é um placeholder documentado como inalcançável na prática (toda rota até
`Build`/`BuildWarmupAsync` passa por `FixedCapacity`/`ElasticCapacity`, que
sempre sobrescreve esse valor) — rounds anteriores citaram esse número como
se fosse o default de produção; não muda a conclusão de H3 (que dependia da
proporção `Factory` ms-s vs. setup ns, não do valor exato), mas registrado
para quem citar de novo. **Achado novo, candidato roteado (Baixo, não
medido)**: `_pendingHeartbeatDisposals` é um `ConcurrentBag<Task>`, mas o
único "escritor lógico" (o pump do heartbeat) retoma em thread arbitrária do
pool a cada pulso após um `await` — sem a afinidade de thread que o tipo é
otimizado para explorar, pode pagar custo de "steal-scan" sem o benefício.
Complexidade pediu revisão de estabilidade antes de qualquer troca estrutural
(esse mecanismo fecha F12/F15). **Decisão do usuário: 3B — deferir para o
Round 8**, quando estabilidade rodar de novo (não medido nem alterado nesta
rodada).

**Desempenho**: confirmou, com rebuild limpo e 2 execuções independentes,
que o benchmark de overhead de observabilidade **não regrediu** desde o
Round 6 (472B/1088B idêntico byte a byte nas 2 rodadas; ~300ns/~600ns,
consistente com a doc) — não é a 3ª ocorrência do padrão de doc stale.
Também mediu em modo completo (sem `--job short`) os 3 benchmarks que
ficaram como "indicativos" desde o Round 1 (`MonitorTickCostBenchmarks`,
`ScaleRejectionCostBenchmarks`, `ElasticAcquireUnderBacklogBenchmarks`) —
números atualizados na entrada do Round 1 acima; nenhum mudou de conclusão.
2 achados textuais Baixo, ambos corrigidos: `usage-observability.md:50`
("~1.9x" vs. o próprio "~2x" medido) e a atualização dos números indicativos
do Round 1 no TODO. Confirmou que o fix O9 (2 `SetTag("success", ...)` novas
em Scale) não introduz alocação evitável nova — mesmo padrão não-cacheado já
existente na vizinhança imediata, e `activity == null` (sem listener, caso
comum em produção) nem avalia o argumento.

**Observabilidade**: confirmou O9/O10/O11 do Round 6 corretos e conectados;
varredura completa de `activity?.SetTag` confirma paridade total entre as 2
Activities existentes e suas métricas irmãs (classe fechada). **2 achados
novos:**
- **[Alto] Falha de warmup deixa `AcquireAsync`/`SwitchToAsync` sem qualquer
  telemetria, indefinidamente**: `EnsureWarmupAsync()`'s `Lazy<Task>` cacheia
  uma falha de warmup inicial (ADR011 — só `WarmupAsync()` explícito instala
  uma tentativa nova). Toda chamada implícita subsequente relançava a mesma
  exceção cacheada *antes* de `_activitySource.StartActivity` sequer rodar —
  nenhum span, nenhuma linha em `acquire.duration`, nenhum log adicional
  (o único `LogError` acontece 1x, dentro de `WarmupCoreAsync`). Um operador
  olhando um dashboard veria silêncio total, não um pico de falha, durante a
  janela em que literalmente toda chamada está falhando. **Decisão do
  usuário: opção 1A** — emitir métrica/Activity (`success=false`,
  `timed_out=false`, `cancelled=false`, status `Error`) em toda chamada
  implícita que relança a falha cacheada, sem repetir `LogError` (evita
  transformar tráfego normal contra um buffer conhecidamente quebrado em
  tempestade de log — mesma razão pela qual o ADR011 já evita retry
  automático aqui). Corrigido com red/green
  (`AcquireAsync_AfterWarmupFailureIsCached_StillRecordsDurationAndActivity`).
- **[Baixo, instância única] Contrato ambíguo do heartbeat no sinal de
  backlog**: um comentário dizia que a espera do heartbeat "must not count"
  no sinal reativo, mas só o *disparo* do `EngineCommand.Backlog()` era
  bloqueado por `countsTowardFaultBudget` — o `Interlocked.Increment(ref
  _waitingCount)` rodava incondicionalmente, e esse mesmo contador é lido
  por `EvaluateBacklogReactive`/`ProcessTick` para calcular o alvo de
  scale-up (efeito real: no máximo +1, autoatenuado, já que só 1 heartbeat
  fica em voo por vez). **Decisão do usuário: opção 2B** — corrigir o
  comportamento (gatear o incremento/decremento por `countsTowardFaultBudget`
  também), não só a prosa, para que a intenção já documentada no comentário
  passe a valer de ponta a ponta. Corrigido com red/green
  (`HeartbeatAcquireWaiting_DoesNotInflateWaitingCount`, via reflection sobre
  `AcquireForHeartbeatAsync`/`_waitingCount`).

**Usabilidade**: confirmou os fixes de doc do Round 6 corretos (banner do
v6-design-proposal.md, ADR004V03/ADR003V03, OnError.md regenerado sem
reincidir na cópia stale conhecida). **2 achados novos, ambos Baixo,
corrigidos como doc-only:**
- 3 citações de rounds internos de auditoria vazando para guias públicos
  (`usage-rabbitmq.md:44`, `usage-dependency-injection.md:58`,
  `usage-observability.md:51`) — reescritas para serem autocontidas
  (inline do raciocínio onde a citação era a única explicação, remoção pura
  onde a frase já se sustentava sozinha).
- `ADR004V03`, seção Links (linhas 116-117): resíduo que a correção anterior
  do Round 6 não alcançou — ainda descrevia a v5.1.0 como release real
  publicada ("bundled into the same 5.1.0 release", "the whole 5.1.0
  batch"), contradizendo o próprio amendment da linha 36 do mesmo arquivo.
  Corrigido, incluindo a referência órfã aos 2 arquivos de `TODO/` já
  deletados na limpeza de descontinuação da v5.1.0.

Também investigada e resolvida uma preocupação de proveniência levantada por
usabilidade: comentários já existentes no código rotulados "Round 7,
Resiliência/Estabilidade" (`RingBufferManager.cs:531,729,1238,1617,1707`)
pareciam sugerir trabalho dessas 2 frentes nesta rodada, mesmo que elas não
tenham sido disparadas. `git blame` confirma que são de 2026-08-21 — de uma
série de auditoria anterior e completamente não relacionada (hardening
pré-v6 em `develop`), coincidência de numeração com esta série (que começou
em 2026-08-23), não um vazamento real.

Verificado: 192/192 testes net10.0 (era 190, +2 novos), build limpo (0
warnings, 0 errors) em toda a solução (3 TFMs, samples, benchmarks, gerador
de docs).

Status: **fechado**.

---

## Round 6 — 2026-08-24

Objetivo: verificar se os fixes do Round 5 (commits `fb6494a`..`ed72f67`) não
introduziram regressão, e achar o que passou batido nas cinco primeiras
rodadas. Grafo do graphify não reatualizado (mesma decisão de custo/benefício
das rodadas anteriores).

**Mudança de enquadramento nesta rodada, por decisão do usuário**: estabilidade
e resiliência tiveram, cada uma, 1 rodada limpa (Round 5, "nada novo") — ainda
não as 2 consecutivas que o critério de convergência exige, então não foram
puladas. Mas, dado esse primeiro sinal, essas 2 frentes receberam um prompt
mais curto/direcionado desta vez (confirmar que os fixes do Round 5 não
regrediram + uma passada fresca mais rápida) em vez do mesmo nível de
profundidade das outras 4 frentes (usabilidade, complexidade, desempenho,
observabilidade), que mantiveram o enquadramento completo de sempre. Se
estabilidade/resiliência convergirem de novo nesta rodada (2 rounds seguidos
sem achado), a redução de escopo pode ser considerada mais agressiva na
próxima; se acharem algo, o enquadramento completo volta na próxima rodada
para essas 2 frentes.

**Estabilidade** (enquadramento reduzido): levantou uma hipótese de leak de
`Activity.Current` entre operações de Scale consecutivas — `DispatchScaleUp`/
`DispatchScaleDown` chamam `_activitySource.StartActivity` de forma síncrona a
partir de `ProcessCommandAsync` (chamado pelo `await foreach` de
`RunEngineAsync`); o `Dispose()` correspondente só roda dentro do corpo forkado
em `Task.Run` do lote, cujo `ExecutionContext` é uma cópia privada tirada no
momento do `Task.Run` — restaurar `Activity.Current` ali não se propaga de
volta para o loop do engine. Essa parte é verdadeira e foi confirmada por uma
reprodução isolada (um método síncrono comum dentro de um loop comum), que
mostrou o leak de fato. Mas construir o mesmo teste dentro do
`RingBufferManager` real (`ConsecutiveScaleOperations_ProduceIndependentRootActivities_NotChainedToEachOther`,
`RingBufferObservabilityTests.cs`) mostrou que o leak **não reproduz** no
código real — investigado e explicado: `AsyncMethodBuilderCore.Start` salva o
`ExecutionContext` do chamador antes de rodar a state machine de um método
async (mesmo seu trecho puramente síncrono, sem nenhum `await` alcançado) e o
restaura quando essa chamada retorna. Então `await ProcessCommandAsync(cmd)`
em `RunEngineAsync` reverte qualquer mutação de `Activity.Current` feita
dentro de `ProcessCommandAsync`/`DispatchScaleUp`/`DispatchScaleDown` assim
que `ProcessCommandAsync` retorna — é essa fronteira de método async, não o
`Task.Run`, que impede o leak aqui. **Terceira vez que uma suspeita de
parent-chaining de Activity é levantada e refutada nesta série de rounds
(Rounds 1, 3, 6)** — vale registrar para a próxima rodada não gastar uma
quarta passada nisso. O teste foi mantido como guarda de regressão permanente,
com o comentário reescrito para descrever o mecanismo que **previne** o bug
(não mais o bug em si), e reforçado com asserções adicionais
(`Parent`/`ParentSpanId`, além de `ParentId`).

**Resiliência** (enquadramento reduzido): nenhum achado novo, fixes do Round 5
confirmados sem regressão — **2ª rodada consecutiva sem achado**, critério de
convergência por frente atingido (junto com estabilidade, cujo único achado
desta rodada foi investigado e refutado, não um bug confirmado). *Verificação:
este parágrafo foi conferido diretamente contra o relatório bruto do subagente
no transcript desta sessão (não inferido da ausência do achado na lista de
pendências pós-compactação) — o subagente de fato reportou "nenhum achado
novo" de forma explícita.*

**Nota sobre o achado de estabilidade**: o próprio subagente classificou a
suspeita de leak de `Activity.Current` como achado de instância única
(reportado por apenas 1 das 6 frentes nesta rodada) e sinalizou
explicitamente que precisava de corroboração 2-de-3 antes de ser tratado como
confirmado, em vez de resolvido unilateralmente. Nenhuma outra frente desta
rodada corroborou o achado independentemente. Em vez de esperar por votos de
outras frentes, resolvi diretamente por reprodução empírica no
`RingBufferManager` real (teste `ConsecutiveScaleOperations_...`) — um padrão
de evidência mais forte que contagem de corroboração, e que produziu um
resultado definitivo (refutado, com mecanismo identificado), não apenas um
placar de votos.

**Observabilidade** (achados O9/O10/O11, todos decididos/corrigidos nesta
mesma sessão):
- **O9 (Médio)**: `scale.operations`/`scale.duration` carregam a tag
  `success`, mas nem `DispatchScaleUp` nem `DispatchScaleDown` chamavam
  `activity?.SetTag("success", ...)` na Activity `"RingBufferPlus.Scale"` —
  terceira instância da mesma classe de assimetria métrica/trace já corrigida
  para Acquire nos Rounds 4-5. Corrigido com red/green
  (`ScaleActivities_CarrySuccessTag_MatchingTheScaleOperationsMetric`);
  varredura de todos os `activity?.SetTag` do arquivo confirma paridade total
  agora entre as 2 Activities existentes (`Acquire`, `Scale`) e suas métricas
  irmãs — nenhuma outra ocorrência da classe.
- **O10 (Alto)**: `usage-observability.md` afirmava que a Activity
  `"RingBufferPlus.Scale"` "correlaciona naturalmente com o resto do seu
  request trace" — falso por construção: `DispatchScaleUp`/`DispatchScaleDown`
  rodam no loop interno do engine da própria instância, nunca no fluxo async
  de nenhum chamador, então todo span `"RingBufferPlus.Scale"` é sempre raiz
  de trace, sem `Parent`. Corrigido apenas na doc (não é um bug de
  comportamento — propagar o contexto do chamador para `auto`/`floor`/
  `backlog` não teria um único chamador para atribuir, seria uma decisão de
  design maior, não um defeito a reparar).
- **O11 (Baixo)**: a mensagem de log `"Stopped Heart Beat item"` sugeria que o
  processamento do item tinha terminado, mesmo no branch em que o callback
  ainda está rodando e o dispose foi deferido (não concluído). Reescrita para
  `"Heart Beat pump iteration finished"`, que descreve o que sempre é
  verdade (o fim da iteração do pump), não o estado do item. Sem teste
  necessário (mudança textual; nenhum teste depende da string antiga,
  confirmado por grep).
- **Baixo, sem número próprio**: strings de `description` dos instrumentos
  `ringbufferplus.scale.operations`/`ringbufferplus.scale.duration` omitiam
  `buffer.name`/`target`/`cancelled` da lista de tags documentadas no próprio
  OTel metadata — corrigido (texto mecânico).
- **Baixo, sem número próprio**: `usage-observability.md:29` usava "mirrors"
  de um jeito ambíguo entre o nome de tag prefixado da métrica
  (`acquire.cancelled`) e o nome sem prefixo da Activity (`cancelled`) —
  clarificado.

**Complexidade + Desempenho**: ambas concluíram independentemente, por
análises estáticas separadas, que o candidato H4 (sinalização de backlog via
`EngineCommand.Backlog()` sem coalescência) é desprezível e não precisa de
medição nem fix — o gate `if (!Elastic || _scaling || CurrentCapacity >=
MaxCapacity) return;` no início de `EvaluateBacklogReactive` já torna
qualquer sinal de backlog redundante barato o suficiente para não justificar
coalescência adicional. **H4 fechado por 2 argumentos estáticos
independentes**, sem necessidade de benchmark.

**Desempenho**: os números re-medidos no Round 5 (`usage-observability.md`)
já estavam stale de novo — 472 B/1048 B foram medidos antes do fix
`cancelled`-tag daquele mesmo round ter sido aplicado, e nunca foram
re-medidos depois. **Isso é a mesma falha de processo do Round 4→5, agora
pela segunda vez seguida** — a causa raiz é medir e documentar
imediatamente após cada commit de código, em vez de esperar todos os fixes de
uma rodada estarem prontos. Corrigido desta vez com uma disciplina de
sequenciamento explícita: todos os fixes de código do Round 6 (O9 incluído)
foram aplicados primeiro, seguidos de rebuild limpo (3 TFMs, 0 warnings) e
**uma única medição real** (`dotnet run -c Release --project
benchmarks/RingBufferPlus.Benchmarks -f net10.0 -- -i --filter
"*ObservabilityOverheadBenchmarks*"`) feita depois de tudo pronto. Número
real: 472 B sem listener (estável, confirmado em todas as rodadas), **1088 B**
com `MeterListener`/`ActivityListener` (não 1048 B) — ~2.3x o alocado não
observado. Tempo: ~307ns sem listener, ~593ns com listener (~1.9x) — a doc
também passou a descrever esses números como ordem de grandeza, não valor
pontual preciso, já que variação de execução-a-execução nesta máquina já
moveu a média em dezenas de ns entre execuções idênticas.

**Usabilidade**: `doc/architecture/v6-design-proposal.md` nunca foi aberto
nas primeiras 5 rodadas apesar de ser referenciado por um ADR atualmente
Aceito (`ADR006V02:24`) e conter reivindicações desatualizadas (versões de
ADR mais antigas que as atuais V02/V03). Corrigido com um banner
"SUPERSEDED" no topo (não deleção — o arquivo é linkado por um ADR vivo,
apagá-lo criaria uma referência quebrada), apontando o leitor para os ADRs
como fonte de verdade atual.

**Achado extra, fora do escopo original das 6 frentes** (descoberto ao
verificar referências cruzadas quebradas pela limpeza de descontinuação da
v5.1.0, decisão do usuário tomada no meio desta rodada): `ADR004V03`
(atualmente Aceito, não histórico) ainda decidia "a próxima release é
versionada 5.1.0, não 6.0.0" e tratava v5.1.0 como uma release passada real
da qual v6.0.0 seria sucessora. Como nenhuma release 5.1.0 chegou a ser
publicada, o usuário confirmou que isso não fere a convenção de imutabilidade
de ADRs deste projeto (que protege decisões que de fato entraram em vigor) —
corrigido diretamente no próprio ADR004V03, seguindo o padrão interno já
existente no arquivo de "Amended on DATE", em vez de criar uma nova versão
via `adrplus`. `ADR003V03` também tinha uma citação órfã para conteúdo do
`CHANGELOG.md` apagado nessa mesma limpeza ("sample window resets across a
scale operation..." não existe mais lá) — corrigida removendo a citação
específica sem alterar a alegação de fundo (o comportamento é real e
continua implementado). `ADR006V02` foi conferido e não precisou de
alteração (já tratava "v5.1.0" apenas como opção considerada-e-rejeitada,
consistente com a decisão nova).

Verificado: 190/190 testes net10.0 (era 188, +2 novos — o teste de
regressão de chaining e o teste de paridade de tag `success`), build limpo
(0 warnings, 0 errors) em toda a solução (3 TFMs, samples, benchmarks,
gerador de docs).

Status: **fechado**.

---

## Round 5 — 2026-08-24

Objetivo: verificar se os fixes do Round 4 (commits `8990c78`..`fb6494a`) não
introduziram regressão, e achar o que passou batido nas quatro primeiras
rodadas. Mesmos 6 ângulos. Grafo do graphify não reatualizado (mesma decisão
de custo/benefício das rodadas anteriores).

**Foco recomendado pelo próprio fechamento do Round 4**
(`acompanhamento-convergencia-v6.md`): (a) confirmar que os 2 Alto do Round 4
(ambos gaps de doc, não bugs de comportamento) realmente não reaparecem — uma
3ª vez seria padrão, não acaso; (b) revisitar H-B/H-C de complexidade só se
houver motivo novo (nenhuma instrução especial para forçar isso). Mesma
atenção de sempre: não confiar em comentários/relatório dizendo "corrigido" —
reler o código real e, quando fizer sentido, reproduzir empiricamente.

### Confirmação dos fixes do Round 4

**Resposta à pergunta (a) do foco recomendado: os 2 Alto do Round 4 não
reaparecem — mas um 3º Alto novo, da MESMA classe (doc citando ADR superseded),
apareceu em outro arquivo.** Não é a mesma instância repetindo; é a mesma
*classe* de bug (referência a ADR superseded) atingindo um arquivo diferente.

- `LifetimeToken()` (TOCTOU de identidade de exceção) — confirmado conectado
  nos 6 pontos por estabilidade E resiliência, independentemente.
  Estabilidade reproduziu empiricamente (1600 hits do caminho disposed, 0
  identidade errada, guard de "só entra na janela real" incluído). Resiliência
  tentou >26.000 corridas e não conseguiu pousar na janela real (evidência do
  tamanho da janela, não do fix estar quebrado) — a prova que sustenta o fix é
  o teste determinístico via reflection já commitado, confirmado passando.
- `BoxedTrue`/`BoxedFalse` — confirmado conectado e sem uso indevido de
  identidade de referência (complexidade + estabilidade, `ReferenceEquals`
  grepado no repo inteiro).
- Comentário do construtor (janela gauge/inicializador) — caracterização
  confirmada correta por complexidade e estabilidade; estabilidade também
  checou um cenário de leak adicional (falha de `init` setter) e confirmou que
  não é alcançável hoje.
- `ringbufferplus.acquire.duration` simétrico nas 3 linhas — confirmado por
  leitura + suite de testes (observabilidade).
- `Logger`/`OnError` mutuamente exclusivos (doc do Round 4) — confirmado
  empiricamente por observabilidade (0 mensagens Logger de nível Error, 1
  chamada OnError, com os dois configurados).
- Números re-medidos de `usage-observability.md` (544B/1168B) — **NÃO
  confirmados**, ver achado de desempenho abaixo.

### Achados novos

- ✅ **[ALTO — usabilidade, corrigido]** `CONTRIBUTING.md:74,78,79` ainda linkava
  `ADR004V02`/`ADR006V01` (superseded) e afirmava que a exceção de v5.0.0 ao
  ciclo de deprecação "does not repeat for any future major" — falso: `ADR004V03`
  (que supersede `ADR004V02`) autoriza explicitamente um 2º reset para v6.0.0,
  e o código já reflete isso (`HeartBeat`, `OnError`, `ElasticCapacity`, etc.
  todos quebraram sem ciclo de `[Obsolete]`). **3ª recorrência da mesma classe
  de bug no mesmo arquivo** — já fechada uma vez para uma versão de ADR
  anterior (`TODO/plano-de-acao.md:80`, achado U-27 da auditoria v5.1) e voltou
  a ficar stale uma versão de ADR depois. Corrigido: as 3 referências trocadas
  para `ADR004V03`/`ADR006V02`, e o texto atualizado para descrever as 2
  exceções reais (v5.0.0 e v6.0.0) em vez de negar a segunda.
- ✅ **[MÉDIO — usabilidade, corrigido]** `CHANGELOG.md:5` contradizia
  `CHANGELOG.md:9` no mesmo arquivo — linha 5 (parágrafo de enquadramento)
  ainda dizia "strict SemVer... from v5.0.0 onward" citando `ADR004V02`,
  enquanto a linha 9 (seção Unreleased) já dizia corretamente "strict SemVer
  resumes from v6.0.0 onward". Mesma causa raiz do achado acima
  (`ADR004V03` não propagada). Corrigido: linha 5 agora lista as 3 exceções
  reais (v5.0.0, v5.1.0, v6.0.0) e cita `ADR004V03`.
- ✅ **[BAIXO — desempenho, corrigido]** `usage-observability.md:49-50`: os
  números re-medidos no Round 4 (544B/1168B) já estavam stale — medidos ANTES
  do fix de cache de boxing daquele mesmo round, nunca re-medidos depois.
  Medição própria e independente desta rodada (2 execuções completas): **472B
  no listener/1048B com listener**, consistente byte a byte. **Esta é a 3ª
  ocorrência da situação "achado Alto do Round 4 era gap de doc" virar
  candidato a converter em padrão** — mas neste caso específico, causada pela
  ordem de operações do próprio coordenador (medir → implementar mais um fix
  → esquecer de re-medir), não por uma frente de auditoria ter deixado passar.
  Corrigido, e o parágrafo reescrito: a comparação "before vs. after" com o
  baseline histórico de 568B (pré-ADR008) não é mais válida como estava
  (472B &lt; 568B, então a moldura "instrumentação custa X% a mais" não se sustenta
  mais contra esse baseline específico, que nunca foi re-medido e não deveria
  ser) — texto ajustado para não fazer essa comparação inválida.
- ✅ **[MÉDIO — observabilidade, corrigido]** Activity `"RingBufferPlus.Acquire"`
  tinha a MESMA classe de assimetria de tag-set que foi o achado principal do
  Round 4 para a métrica `acquire.duration` — só que no lado do trace: a tag
  `cancelled` só era setada no catch de cancelamento do chamador
  (`RingBufferManager.cs`, antiga linha ~468), ausente (não `false`) nas 2
  outras linhas (sucesso, timeout/shutdown). Passou 4 rounds sem ser pego
  porque o Round 4 corrigiu só o histograma, não as 2 chamadas `SetTag` de
  sucesso. Doc (`usage-observability.md:35`) já descrevia esse comportamento
  como intencional ("`cancelled` when the caller's own token... ended the
  call"), então doc e código batiam entre si — mas ambos estavam errados pelo
  mesmo padrão já identificado no Round 4. Corrigido: `cancelled=false`
  adicionado nas 2 linhas que faltavam (uma via `BoxedFalse`, caminho de
  sucesso; a outra com `false` cru, mesmo estilo do restante daquele branch
  frio); doc reescrita para descrever a tag como sempre presente. Red/green
  feito estendendo os 2 testes de Activity já existentes.
- **[Candidato não medido — complexidade, encaminhado para desempenho +
  estabilidade, sem ação nesta rodada]** H4: cada `AcquireAsync` elástico que
  cai no ramo de espera grava um `EngineCommand.Backlog()` no canal ilimitado
  `_commands`, sem coalescência — sob backlog sustentado perto de
  `MaxCapacity` com muitos chamadores concorrentes esperando, o volume desses
  comandos tende a acompanhar 1:1 o número de esperadores nesse instante, e
  todos competem pelo mesmo loop serial de único consumidor que também
  processa `FactoryBatchCompleted`/`ReplaceOne`/o floor guard — risco de
  enfileiramento, não de tamanho de alocação (distinto do H-B já descartado).
  Não medido nesta rodada; se confirmado por medição em rodada futura, requer
  aprovação de estabilidade antes de qualquer mitigação (histórico do projeto
  já registrou uma reversão de otimização que reabriu um risco de correção já
  fechado nesta mesma área de sinalização).

### Fechamento do Round 5

Todos os 4 achados de comportamento/doc decididos e corrigidos nesta mesma
sessão (2 Alto, 1 Médio de doc, 1 Médio de código com red/green); o candidato
H4 fica em aberto, explicitamente não medido, para uma rodada futura decidir.
Nenhum achado ficou sem decisão. Verificado: 188/188 testes net10.0, build
limpo (0 warnings, 0 errors) em toda a solução a cada etapa. 3 das 6 frentes
(estabilidade, resiliência) não encontraram nada novo além do que já está
listado — primeiro sinal real de aproximação à convergência para essas duas
frentes especificamente, mesmo que o total de achados do round (4, sem contar
H4) não tenha caído em relação ao Round 4 (10) por estarem em categorias
diferentes de achado (doc vs. comportamento).

Status: concluído.

---

## Round 4 — 2026-08-24

Objetivo: verificar se os fixes do Round 3 (commits `af84bd1`..`8990c78`) não
introduziram regressão, e achar o que passou batido nas três primeiras rodadas.
**Atenção redobrada desta vez**: os Rounds 2 e 3 tiveram cada um um caso de fix
"confirmado como corrigido" que na verdade não estava — o helper `SafeIsEnabled`
foi criado no Round 2 mas só conectado de fato no Round 3, e isso só foi pego
porque 3 frentes reproduziram empiricamente o cenário, não porque testes/build
falharam. Cada frente deste Round 4 foi instruída a não confiar em comentários
de código/relatório que dizem "corrigido" — reler o código real e, quando fizer
sentido, reproduzir empiricamente antes de aceitar. Mesmos 6 ângulos. Grafo do
graphify não reatualizado (mesma decisão de custo/benefício das rodadas
anteriores).

### Confirmação dos 4 fixes do Round 3 (por leitura direta + reprodução, não por confiar no relatório)

Nenhuma repetição do padrão "comentário diz corrigido, código não conecta" dos
Rounds 2/3 — cada um dos 4 fixes foi confirmado por pelo menos 2 frentes
independentes, e o guard `SafeIsEnabled` foi confirmado com vermelho→verde real
(estabilidade e resiliência reverteram temporariamente a linha, reproduziram a
falha, restauraram e reconfirmaram verde):

- `SafeIsEnabled` conectado em `RingBufferManager.LogMessage` — confirmado por
  estabilidade (vermelho→verde), resiliência (vermelho→verde + teste existente),
  desempenho (fora do caminho quente), observabilidade (leitura + teste).
- `Stopwatch.GetTimestamp`/`GetElapsedTime` + delegate `_turnbackDelegate`
  cacheado — confirmado por estabilidade (sem race, campo `readonly` setado antes
  de qualquer uso), resiliência (sem mudança de comportamento sob shutdown),
  complexidade (conectado, sem custo novo), desempenho (496 B confirmado com
  medição própria em 2 processos independentes, redução real de 104 B/17,3%).
- Comentário sobre CTS/timer eager permanecer como está — confirmado consistente
  com o código real (`!linked.IsCancellationRequested` antes do fast path
  intacto) por resiliência e complexidade; observabilidade e usabilidade
  concordam que não precisa de reflexo em guia público (mesmo precedente do "4x
  amplification").
- `RingBufferBuilder.LogError` usando `message.Message` — confirmado por
  resiliência e observabilidade (exceção completa ainda anexada, `ReferenceEquals`
  verificado); observabilidade nota que a alegação de "paridade" do commit não é
  literalmente exata (ver achado O3 abaixo).

### Achados novos

- ✅ **[ALTO — usabilidade, corrigido]** `src/RingBufferPlus/README.txt:44,50`
  ("What's new"): afirma "v5.0.0 (latest version)" e que `AutoScaleAcquireFault`/
  `SwitchToAsync` manual são "mutually exclusive at the type level" — os dois
  falsos sob v6 (`AutoScaleAcquireFault` foi removido inteiramente; todo pool
  elástico suporta `SwitchToAsync` incondicionalmente). O arquivo se
  autocontradiz: 37 linhas depois (`:87`) já descreve corretamente o
  comportamento v6. Miss do próprio Round 3 (`relatorio-auditoria-v6.md:467`
  registrou "lido integralmente, sem achado" — o conteúdo já estava lá).
  Empacotado no `.nupkg` (`RingBufferPlus.csproj:58-61`, `Pack=True`) — superfície
  visível de usuário real, não só doc interna.
- ✅ **[ALTO — observabilidade, corrigido via doc]** `doc/adr/ADR007V03-redesign-of-the-public-fluent-api-surface.md:52`
  e `doc/guides/usage-observability.md:57-65` ainda descrevem `Logger`/`OnError`
  como um par que entrega o mesmo evento junto — mas o código
  (`RingBufferManager.cs:1910-1922`, `RingBufferBuilder.cs:324-354`) implementa os
  dois como **mutuamente exclusivos** para eventos de erro: com `OnError`
  configurado, `Logger` nunca recebe nada de nível Error. Essa semântica de
  substituição já tinha sido decidida e fixada nos XML docs das 3 interfaces
  (`IRingBufferBuilder`/`IRingBufferFixedBuilder`/`IRingBufferElasticBuilder`) no
  Round 1 — o gap é que o ADR e o guia de observability nunca foram atualizados
  para bater com essa decisão já tomada, não uma decisão nova. Confirmado
  empiricamente pela frente (harness standalone, Logger recebe 0 mensagens Error
  quando OnError está configurado). Não é uma mudança de comportamento — é
  alinhar 2 docs à decisão do Round 1.
- ✅ **[MÉDIO — resiliência, corrigido]** TOCTOU entre o guard
  `ObjectDisposedException.ThrowIf(_disposed, this)` e o uso subsequente de
  `_lifetime.Token` em `AcquireCoreAsync` (`RingBufferManager.cs:298→321`),
  `SwitchToAsync` (`:437→444`) e `WarmupAsync`/`WarmupCoreAsync` (`:481→659,663`).
  `DisposeAsync` seta `_disposed=true` de forma síncrona (linha 504) mas só chama
  `_lifetime.Dispose()` no fim do `finally` (linha 643), depois de aguardar tasks
  em voo. Um chamador que passa pelo guard antes de `_disposed` virar true, mas
  cuja continuação só executa depois de `_lifetime.Dispose()` já ter corrido
  (starvation de thread pool, ou `DisposeAsync` terminando rápido sem heartbeat
  pendente), encontra `_lifetime.Token` já descartado —
  `CancellationTokenSource.get_Token()`/`CreateLinkedTokenSource` lançam
  `ObjectDisposedException`, mas com `ObjectName` referenciando o
  `CancellationTokenSource` em vez do `RingBufferManager`. Sem leak/corrupção/hang
  (`TurnbackAsync` já trata `ChannelClosedException` no caminho concorrente de
  drain) — só uma mensagem de exceção confusa que pode ser lida como bug interno
  em vez de shutdown gracioso esperado. Reproduzido empiricamente pela frente
  (probe descartável, 8 `AcquireAsync` concorrentes vs. `DisposeAsync`, removido
  após o teste).
- ✅ **[MÉDIO — observabilidade, corrigido]** `ringbufferplus.acquire.duration`:
  o caminho de sucesso (`RingBufferManager.cs:361-363`) emite só `buffer.name` e
  `acquire.success=true` — sem as chaves `acquire.timed_out`/`acquire.cancelled`,
  que os dois caminhos de falha sempre emitem. Viola o próprio princípio já
  declarado no código para `scale.*`
  (`DispatchScaleDown`/`RingBufferManager.cs:1154-1156`: "'cancelled' is still
  always emitted (false) to preserve the existing tag contract... not just the
  ones where it can actually be true"), só que esse princípio nunca foi aplicado
  a `acquire.duration`. Um consumidor Prometheus/OTLP filtrando
  `acquire.timed_out="false"` esperando capturar "chamadas sem timeout" obtém
  zero resultados para todo acquire bem-sucedido (a maioria do tráfego), porque o
  rótulo simplesmente não existe nessas séries em vez de existir como `false`.
  Confirmado empiricamente via `MeterListener` num harness standalone.
- **[BAIXO — estabilidade, documentado como limitação conhecida, não corrigido]**
  `RingBufferBuilder.BuildCore` (`RingBufferBuilder.cs:182-201`) constrói o
  manager via sintaxe de inicializador de objeto — o construtor
  (`RingBufferManager.cs:269-284`) já inicia `_engineTask` e registra o
  `ObservableGauge` de `ringbufferplus.capacity.current` **antes** que `Name`/
  `Capacity`/demais propriedades `required` (sem inicializador inline) sejam
  atribuídas pelo inicializador de objeto externo. Se um `MeterListener` ativo
  fizer uma coleta de background exatamente nessa janela de poucas instruções,
  a medição resultante tem `buffer.name=null`. Sem corrupção de estado nem crash
  — só uma leitura de telemetria espúria. Instância única (não corroborado por
  outra frente), janela impossível de forçar sem instrumentar produção (violaria
  o mandato read-only). Fechar isso de verdade exigiria desacoplar o início do
  engine/registro do gauge da sintaxe de inicializador de objeto do builder —
  redesenho do acoplamento builder↔manager, não um fix pontual. Mesmo formato de
  decisão do trade-off "CTS/timer eager" do Round 3: documentado como comentário
  no código, não elevado a ADR.
- ✅ **[BAIXO — usabilidade, corrigido]** `doc/guides/usage-observability.md:48-50`
  cita 592 B/1216 B para overhead de listener sem ancorar a um commit, mas o
  caminho medido (`AcquireCoreAsync`) mudou nesta mesma sessão (fix de alocação
  do Round 3, 600 B→496 B) sem os números deste guia terem sido re-medidos.
  Re-medido nesta sessão (ver Round 4 fechamento) e atualizado.
- **[BAIXO — observabilidade, não corrigido — nuance textual, não funcional]**
  O comentário do commit `df50203` (Round 3) que justificou `message.Message` em
  `RingBufferBuilder.LogError` como alcançando "paridade" com
  `RingBufferManager.LogError` não é literalmente exato — o texto do
  `RingBufferManager` (`RingBufferManager.cs:1915`) continua sendo uma string
  composta com timestamp manual e prefixo de nome
  (`$"{DateTime.Now:...} {Name}: {error.Message} "`), não um `error.Message` cru.
  Nenhuma perda funcional (a `Exception` completa continua anexada nos dois
  casos) — é só uma imprecisão na justificativa do commit, não no comportamento.
  Não corrigido: mudar `RingBufferManager.LogError` para bater literalmente
  exigiria remover o timestamp manual, o que é uma mudança de formato de log
  observável para consumidores existentes, fora do escopo deste achado.
- ✅ **[H-A — complexidade, corrigido]** boxing de `bool` nas tags de métrica do
  caminho de sucesso de `AcquireCoreAsync` — agravado pelo próprio fix da tag
  symmetry acima (2 chaves novas, medidas em +48 B/op). Corrigido: dois campos
  estáticos `BoxedTrue`/`BoxedFalse` reutilizados nas 3 `KeyValuePair`s e nas 2
  chamadas `activity?.SetTag` do caminho de sucesso — escopo deliberadamente
  restrito a esse caminho (medido), não estendido aos branches de falha/`scale.*`
  (frios, sem benefício medido). Medido com `AcquireThroughputBenchmarks -i`:
  544 B (pós tag-symmetry, pré cache) → **472 B** — líquido abaixo dos 496 B do
  Round 3, apesar das 2 tags novas.
- **[H-B — sem ação]** `EngineCommand.Backlog()`/`ReplaceOne()`/`Tick()` alocando
  um record sem estado por chamada (`RingBufferManager.cs:347,731,1793,1875`) —
  confirmado fora do caminho rápido medido (ramo de suspensão em `ReadAsync`);
  valor questionável nos defaults atuais, mesmo padrão de descarte do H1/H2 do
  Round 1.
- **[H-C — sem ação, corretamente descartado pela própria frente]**
  `RingBufferValue<T>` não pode virar `struct` — bloqueado por correção, não por
  desempenho: o guard de dispose atômico (`Interlocked.Exchange(ref _disposed, 1)`,
  `CHANGELOG.md:53`) depende de identidade única por lease; uma cópia por valor
  reabriria a corrida de double-turnback já fechada.

### Fechamento do Round 4

Todos os achados decididos/corrigidos nesta mesma sessão (10 achados: 2 Alto,
2 Médio, 1 Baixo corrigido, 1 Baixo documentado como limitação conhecida, 1 Baixo
sem ação por trade-off de escopo, 1 H corrigido, 2 H sem ação). Nenhum ficou em
aberto sem decisão. Verificado: 188/188 testes net10.0 (era 187, +1 novo — o
teste de identidade da `ObjectDisposedException`; o teste de tag symmetry
estendeu um teste já existente), build limpo (0 warnings, 0 errors) em toda a
solução (3 TFMs, samples, benchmarks, gerador de docs). Red/green feito para os
2 achados de comportamento (TOCTOU de exceção, tag symmetry) — o primeiro via
reprodução determinística do contrato do fix (`LifetimeToken()` via reflection,
já que a janela de corrida real não é forçável deterministicamente sem
instrumentar produção) mais uma validação empírica probabilística descartável
(3842 hits, 0 identidade errada pós-fix; 1 identidade errada pré-fix em volume
comparável) que não foi commitada por ser lenta/instável como teste permanente.

Status: concluído.

---

## Round 3 — 2026-08-24

Objetivo: verificar se os fixes do Round 2 (commits `29cbcf0`..`cf067b2`) não
introduziram regressão, e achar o que passou batido nas duas primeiras rodadas.
Mesmos 6 ângulos. Grafo do graphify não reatualizado (mesma decisão de custo/
benefício do Round 2 — mudança incremental).

Status: concluído (todos os 7 achados decididos/corrigidos).

### auditoria-complexidade — concluída

- Verificadas as 4 mudanças de código do Round 2 (`SafeIsEnabled`,
  `ContinueWith` novo no dispose adiado, `AddSingleton<IHostedService>`,
  `LogError` passando exceção real) — **nenhuma introduziu custo por operação
  novo**. `SafeIsEnabled` só é chamado a cada ~300ms (Monitor) ou no build-time,
  nunca no caminho de sucesso de `AcquireAsync`.
- **[BAIXA-MÉDIA, 2ª derivação independente da mesma hipótese do Round 2, ainda
  não medida]** Reforçou o achado do "CTS/timer por requisição" em
  `AcquireCoreAsync` (`RingBufferManager.cs:297-299`) — confirmou que o
  benchmark citado no Round 2 (`AcquireThroughputBenchmarks`, 592B/331.7ns) é de
  **2026-08-12, anterior a toda a auditoria**, nunca re-rodado especificamente
  para essa pergunta. Achou 2 fontes adicionais de alocação no mesmo trecho
  quente: `Stopwatch.StartNew()` (linha 297, aloca objeto — trocável por
  `Stopwatch.GetTimestamp()`/`GetElapsedTime`, sem alocação) e o delegate de
  `TurnbackAsync` (linha 344, conversão de grupo de método captura `this` a
  cada chamada de sucesso, não cacheado em campo). Propôs um experimento A/B de
  3 braços (Stopwatch, delegate, CTS preguiçoso) para `auditoria-desempenho`
  medir isoladamente, e sinalizou que uma versão "CTS preguiçoso" do fix
  precisaria preservar a checagem de cancelamento na linha 309 (risco de
  correção, não só desempenho) — `auditoria-estabilidade` precisa revisar isso
  antes de qualquer fix nessa parte específica.
- Sem achado: caminho feliz de `TurnbackAsync`, `FloorGuardDecision.cs`, loops
  de drenagem/prune (todos limitados por quantidade requisitada, não por
  crescimento contínuo).

### auditoria-estabilidade, auditoria-resiliencia, auditoria-observabilidade — CONFIRMADO 3/3 INDEPENDENTES

- ✅ **[CRÍTICO/ALTO — corrigido, 3/3 confirmações independentes]** O fix
  `SafeIsEnabled` do Round 2 foi **escrito mas nunca conectado** no call site
  que deveria proteger: `LogMessage` em `RingBufferManager.cs:1874` continuava
  chamando `Logger.IsEnabled(LogLevel.Debug)` cru — o helper `SafeIsEnabled`
  existia (linha 1929) mas era código morto (zero call sites, confirmado por
  `grep` em 3 frentes independentes). O commit `41ef798` e o relatório desta
  auditoria afirmavam esse achado como fechado — **estava errado**: só
  `RingBufferBuilder.cs` foi corrigido de fato, não `RingBufferManager.cs` (o
  arquivo com o maior blast radius). As 3 frentes reproduziram empiricamente,
  de forma independente, o mesmo cenário: um `Logger` cujo `IsEnabled` lança
  derruba `WarmupCoreAsync`/`BuildWarmupAsync` inteira (pior que o cenário
  original do comentário, que citava só `ProcessTick`/`RunHeartbeatAsync`) —
  e mataria `_engineTask`/`_heartbeatTask` permanentemente e silenciosamente
  nesses dois casos. Nenhuma das duas rodadas anteriores tinha um teste
  cobrindo "IsEnabled lançando" — por isso o build limpo (0 warnings) e os
  186/186 testes verdes não pegaram a regressão. Corrigido (uma linha:
  `SafeIsEnabled(Logger, LogLevel.Debug)`). Red/green feito
  (`WarmupAsync_WhenLoggerIsEnabledThrows_StillCompletes`).
- Confirmado (resiliência, 3 buffers do mesmo T): o fix `AddSingleton<IHostedService>`
  funciona corretamente — todos recebem hosted service e warmup.
- Confirmado (resiliência): `[FromKeyedServices]` isola corretamente, não
  constrói o buffer irmão quebrado.
- Investigado e descartado (resiliência): double-dispose via singleton
  "encaminhador" + keyed apontando pra mesma instância — acontece a nível de
  container, mas `_disposeGuard` já absorve com segurança (idempotência
  pré-existente, não um bug).
- ✅ **[BAIXO, pré-existente — corrigido 2026-08-24]** (observabilidade)
  Assimetria de formato entre `RingBufferBuilder.LogError` (gravava
  `Exception.ToString()` completo: tipo + mensagem + stack trace) e
  `RingBufferManager.LogError` (grava só `error.Message`) — ambos internamente
  consistentes, mas verbosidade diferente entre as duas classes para um sink
  estruturado lendo o campo de texto. Corrigido: `RingBufferBuilder.LogError`
  agora usa `message.Message` (curto), igual ao irmão — a exceção completa
  (tipo, stack trace) já está disponível via o parâmetro `Exception` anexado
  separadamente (fix do Round 2). Não é regressão desta sessão.

### auditoria-usabilidade — concluída

- ✅ **[MÉDIO — corrigido]** `doc/guides/concepts.md:52` ainda usava a ordem
  antiga de parâmetros `ElasticCapacity(init, min, max, ...)` — contradiz o
  próprio arquivo (diagrama mermaid 14 linhas acima já usa a ordem certa),
  o `CHANGELOG.md` (que documenta a mudança de ordem explicitamente), e todos
  os outros guias/samples. Única ocorrência restante da ordem antiga no repo.
- ✅ **[BAIXO — corrigido]** `usage-dependency-injection.md:56` tinha redação
  remanescente ("searches among services registered") quase idêntica à frase
  que a correção da linha 49 (Round 2) explicitamente descartou como o
  mecanismo antigo — a conclusão continua verdadeira, só a redação ficou
  desatualizada ao lado do fix.
- ✅ **[BAIXO — corrigido]** `usage-observability.md:57-63` catálogo de
  mensagens do heartbeat estava incompleto — faltava a 4ª ramificação
  (`"Heart Beat cancelled by shutdown after the callback had already
  finished."`, `RingBufferManager.cs:1801-1811`, quando o shutdown ocorre mas
  o callback já tinha terminado).
- ✅ **[BAIXO, fraco/opcional — corrigido junto]** `concepts.md:24` linkava
  `ElasticCapacity` para o guia de pin manual em vez do guia de autoscale
  (que é o dedicado a explicar `ElasticCapacity` em si).
- Verificado e sem achado: README.txt (lido integralmente), overview.md,
  ADR007V03, todos os outros guias, todos os samples, docs de API geradas,
  CHANGELOG.md (seção Unreleased não precisa listar fixes internos do audit
  de uma versão ainda não lançada).

### auditoria-desempenho — concluída

- ✅ **[MÉDIO — pendência do Round 2/3 de complexidade fechada com número
  real, não é regressão]** O padrão CTS/timer por requisição em
  `AcquireCoreAsync` (`RingBufferManager.cs:297-299`) responde por **~416 dos
  600 bytes/operação (≈69%)** no caminho rápido (`AcquireThroughputBenchmarks`,
  sem contenção) — decompôs isoladamente: `new CancellationTokenSource(timeout)`
  (144B) + `CreateLinkedTokenSource` de 3 tokens (128B), com efeito de sinergia
  levando a 416B juntos (mais que a soma das partes). No caminho rápido, o
  CTS/timer é criado e descartado **sem nunca ser efetivamente consultado**
  (`TryRead` tem sucesso antes de qualquer wait) — overhead puro nesse caso.
  Não é regressão do Round 2/3 (código pré-existente, nunca medido antes desta
  auditoria).

  ✅ **Fixes de baixo risco aplicados nesta sessão** (parte do risco identificado
  pela complexidade, sem mexer em semântica de cancelamento): `Stopwatch.StartNew()`
  → `Stopwatch.GetTimestamp()`/`GetElapsedTime` (elimina alocação do objeto
  `Stopwatch`) e delegate de `TurnbackAsync` cacheado em campo `readonly`
  (elimina alocação de closure por chamada de sucesso). Medido: **496B, redução
  real de ~104B/operação** (de 600B). A parte de maior risco (construir o
  CTS/timer de forma preguiçosa, só quando o fast path falha) — decisão do
  usuário (2026-08-24): **manter como está, comentário no código** (mesmo
  padrão do trade-off "4x amplification" em `CreateItemsAsync` — não é escala
  de ADR, não introduz nada novo). Refatorar exigiria hoisting de
  `timeoutCts`/`linked` para fora do escopo condicional (os `catch` abaixo
  precisam inspecionar `timeoutCts.IsCancellationRequested`) e trocar a
  checagem `!linked.IsCancellationRequested` pré-fast-path por uma nos tokens
  crus — em um método com histórico extenso de fixes de corretude de
  cancelamento (R11/R15/R17/O1/O2/O6 citados nos próprios comentários).
  Comentário adicionado em `RingBufferManager.cs` (linha ~304) documentando o
  número medido e o raciocínio, para não ser re-perguntado no futuro.
- Reconfirmado com número próprio (não copiado): fix `SafeIsEnabled`/guard
  ~38x mais rápido no caminho gated (5.6ns vs 212ns), impacto absoluto
  desprezível — bate com o Round 2.
- **[BAIXO]** Custo marginal do `ContinueWith` novo no dispose adiado do
  heartbeat: ~57B/op, mas só no branch de timeout (raro, limitado pela
  cadência do heartbeat, não pelo throughput de requisições) — irrelevante.
- `AddSingleton<IHostedService>` (Round 2): não é um caminho de execução
  repetido (roda uma vez por `AddRingBuffer<T>` na composição de DI), não
  mensurável como "custo de runtime" — caracterizado honestamente como
  trade-off aceito (mais trabalho real no startup, correção restaurada vs.
  antes), não pendência de desempenho.

---

## Round 2 — 2026-08-24

Objetivo: verificar se os fixes do Round 1 (commits `a9eaac1`..`d87e90c`, ver
`plano-de-acao-v6.md`) não introduziram regressão, e achar o que passou batido na
primeira rodada. Mesmos 6 ângulos do Round 1. Grafo do graphify NÃO reatualizado
desta vez (mudança incremental pequena — 6 commits de fix, não uma reestruturação;
decisão de custo/benefício, não uma omissão).

Status: em andamento.

### auditoria-desempenho — concluída (retentativa)

- ✅ **[BAIXO — confirmado com número real]** Confirmou empiricamente o achado da
  complexidade sobre `LogMessage`: sem o guard `IsEnabled`, cada tick pagava
  ~220ns/344B mesmo com Debug desligado; com o guard (já corrigido nesta sessão),
  cai para ~5.6ns — ganho de ~39x em tempo, mas escala absoluta desprezível
  (~0.7µs/s por buffer elástico). Não é regressão do v6 — o gap já existia, o
  Round 1 só copiou o comentário errado para um call site de maior frequência.
  Benchmark novo `MonitorTickLogGuardBenchmarks.cs` adicionado ao repo (mede o
  guard isoladamente, sem depender de `RingBufferManager` inteiro).
- Nenhuma regressão de desempenho encontrada nas mudanças do Round 1
  (`DispatchScaleUp`/`DispatchScaleDown`'s tags novas, `LogWarning` condicional).

### auditoria-estabilidade — concluída (retentativa)

- ✅ **[ALTO — corrigido, encontrado no próprio fix desta sessão]** O guard novo
  `!Logger.IsEnabled(LogLevel.Debug)` adicionado ao `LogMessage` (correção do achado
  de complexidade acima) **não estava protegido por `SafeInvokeSink`** — violação do
  invariante F23 já documentado no arquivo ("uma chamada para Logger/ErrorHandler do
  consumidor é código não confiável, nunca pode escapar para quebrar um pump
  compartilhado"). Confirmado empiricamente (sonda descartável) que
  `Microsoft.Extensions.Logging.Logger.IsEnabled` (a implementação que a maioria dos
  consumidores via DI usa) agrega e relança exceções de provider — um provider
  disposado antes do manager (ordem de shutdown do host não é garantida) faria
  `Logger.IsEnabled` lançar, matando `_engineTask` ou `_heartbeatTask`
  permanentemente e silenciosamente (sem `ObjectDisposedException` visível ao
  chamador — o buffer continua respondendo a `AcquireAsync` normalmente, só sem
  escalar/heartbeat). Corrigido: novo `SafeIsEnabled(logger, level)` (mesmo padrão
  `try/catch` de `SafeInvokeSink`, trata exceção como "não habilitado"). Mesma classe
  de bug pré-existente (não desta sessão) em `RingBufferBuilder.cs:315,331` —
  registrada mas não corrigida ainda (blast radius bem menor, só build-time).
- ✅ **[MÉDIO/BAIXO — corrigido]** Duas imprecisões na mensagem `LogWarning` do fix
  do heartbeat: "capacity was already corrected" só é verdade no caminho
  `!healthy`/Invalidate (removida da mensagem); "orphaned heartbeat callback(s)" em
  `DisposeAsync`'s warning de grace-period é impreciso para entradas vindas do novo
  branch saudável (onde o callback já retornou, só o dispose está lento) — reescrita
  para "pending heartbeat item dispose(s)", cobrindo os dois casos. Doc
  (`usage-observability.md`) atualizada para bater com o texto novo.
- Verificado e sem achado: `_disposed volatile`, padrão prune+re-add reaproveitado no
  branch saudável (sem race nova), robustez da checagem `scaleTrigger == "manual"`
  (confirmado que `DispatchScaleDown` só é chamado com `"manual"` ou `"auto"`, nunca
  outro valor), thread-safety do campo `Logger` em si (imutável após construção,
  `init`-only).
- ✅ **[BAIXO, pré-existente — corrigido por decisão do usuário 2026-08-24]**
  `RingBufferBuilder.cs`'s mesma classe de bug do guard `IsEnabled` desprotegido
  (linhas 315, 331) — corrigido com o mesmo `SafeIsEnabled` já usado em
  `RingBufferManager.cs`. Fechou a classe completa nesta sessão, não só a instância
  de maior blast radius.

### auditoria-resiliencia — concluída (a mais demorada, ~65min de investigação real)

- ✅ **[ALTO — corrigido, achado NOVO não relacionado ao Round 1]** `AddHostedService<T>(factory)`
  registra via `TryAddEnumerable`, que deduplica por `(ServiceType, ImplementationType)`.
  Como `RingBufferWarmupHostedService<T>` é o MESMO tipo fechado para todo
  `AddRingBuffer<T>` do mesmo `T` (independente do `buffername`), toda chamada além
  da primeira para um dado `T` registrava **zero** `IHostedService` de verdade —
  silenciosamente, sem exceção, sem log. Confirmado lendo o código-fonte real do
  runtime (`dotnet/runtime`) e empiricamente (probe: 2 buffers do mesmo T → só 1
  `IHostedService` registrado, só o 1º recebe warmup automático). Pré-existente desde
  `de5d8ce` (ADR007V03), não uma regressão do Round 1 — mas é exatamente o tipo de
  "miss" que o Round 2 existe para pegar (o teste de regressão do Round 1 não via
  esse bug porque constrói o hosted service manualmente em vez de resolver via DI).
  Severidade Alto porque `EnsureWarmupAsync` (o warmup implícito de
  `AcquireAsync`/`SwitchToAsync`) não tem retry — um buffer que nunca recebeu o
  warmup automático fica com `_warmup` permanentemente faltado se a 1ª tentativa real
  tiver um hiccup transiente, e `usage-dependency-injection.md` documentava esse
  cenário (múltiplos buffers do mesmo T) como suportado, afirmando "warmup é
  automático" — falso para todos além do 1º. Corrigido: `AddSingleton<IHostedService>`
  (aditivo, não deduplica por tipo) em vez de `AddHostedService`. Red/green feito.
- ✅ **[MÉDIO — corrigido, confirmado 2/2 já que a estabilidade também achou o mesmo
  guard desprotegido por outro ângulo]** Falha tardia do dispose adiado no branch
  saudável do heartbeat (mesmo fix do Round 1) não era observada — ao contrário do
  branch F12/F15 irmão (que envolve seu dispose adiado num `ContinueWith` que loga a
  falha), a task crua era adicionada direto no bag. Confirmado empiricamente com um
  probe cuidadosamente isolado (flag de hang por instância, não um evento
  compartilhado — evita que o outro item idle do pool mascare o resultado via seu
  próprio observador já correto). Corrigido: mesmo padrão `ContinueWith` da F12/F15.
  Red/green feito (isolando a mesma armadilha de mascaramento que a auditoria
  descreveu).
- ✅ **[MÉDIO — resolvido via doc, decisão do usuário 2026-08-24]** O padrão
  documentado `IEnumerable<IRingBufferService<T>>` (workaround para resolver
  múltiplos buffers do mesmo T por construtor plano) ainda constrói TODOS os buffers
  registrados daquele T quando resolvido — undermina o próprio propósito do fix de
  acoplamento do Round 1 (commit `50a2f9b`) para esse caminho específico de acesso.
  Confirmado empiricamente: um buffer "bad2" quebrado faz
  `GetServices<IRingBufferService<int>>()` lançar mesmo quando o consumidor só
  queria "good2". Não é um "bug de DI" no sentido usual — `IEnumerable<T>` construir
  todas as implementações é semântica padrão de DI — mas contradizia a documentação
  que recomendava esse padrão como "a" forma de resolver múltiplos buffers. Usuário
  decidiu remover a recomendação de `IEnumerable`. Fui além do "só remover": propus
  e verifiquei empiricamente (probe descartável, console app puro sem ASP.NET Core)
  que `[FromKeyedServices(buffername)]` — usando a MESMA chave de DI que o hosted
  service já usa — resolve corretamente por construtor plano em qualquer host
  (não é feature exclusiva de MVC), dando isolamento de verdade nesse caminho.
  `usage-dependency-injection.md` atualizado com a alternativa correta.
- Verificado e sem achado: o fix do hang do `DisposeAsync` em si (confirmado via
  teste existente + leitura, retorna em ~2×`PulseHeartBeat` no pior caso);
  `_disposed volatile`; validação `PulseHeartBeat > 0` (grep na suíte inteira, nenhum
  uso legítimo de zero); duas chamadas `AddRingBuffer<T>` com o MESMO `buffername`
  (seguro, "last wins" via keyed DI, na prática subsumido pelo Achado 1 de qualquer
  forma); coexistência de singleton keyed/não-keyed e double-disposal (`_disposeGuard`
  já cobre).

### auditoria-usabilidade — concluída

- ✅ **[MÉDIO-ALTO — corrigido]** `usage-dependency-injection.md:49` ainda descrevia o
  mecanismo de lookup PRÉ-fix ("looks up the singleton by name among every
  `IRingBufferService<T>`"), exatamente o que o fix do acoplamento de DI do Round 1
  eliminou — o texto usava quase as palavras invertidas do próprio comentário do fix
  ("not by enumerating and filtering every..."). Doc não tocada no Round 1, achado
  exatamente do tipo que a tarefa pediu para caçar. Corrigido para descrever a
  garantia de isolamento nova.
- ✅ **[MÉDIO — corrigido]** `usage-heartbeat.md:45,51` ainda afirmava que um `false`
  com `Dispose()` travando "stalls this pump until it returns" e é "the same shape of
  unbounded wait" que `Invalidate()` — ambas as frases contradiziam diretamente o fix
  do hang de `DisposeAsync` do Round 1 (agora limitado a `PulseHeartBeat`). Corrigido
  para descrever o comportamento limitado e deferido atual.
- ✅ **[BAIXO-MÉDIO — corrigido]** A nova mensagem `LogWarning` do fix do heartbeat
  ("Heart Beat item dispose did not complete within one pulse...") não aparecia em
  nenhum guia — `usage-observability.md`'s seção de Logging enumerava só 4 categorias.
  Adicionada como 5ª categoria.
- ✅ **[MÉDIO — corrigido, corroborado independentemente pela observabilidade com
  prova empírica]** `usage-observability.md:43` superestimava a cobertura do log de
  Debug do Monitor ("this is the only way to answer why didn't it scale") — na
  verdade não loga nada durante um episódio "ativo" (demanda saturando capacidade),
  nem quando o Tick é pulado por `_scaling`/pin manual ativos. Corrigido.
- Verificado e sem achado: README.txt, overview.md, concepts.md, ADR007V03,
  usage-elastic-autoscale.md, usage-rabbitmq.md, OnError nas 3 interfaces, docs de
  API geradas, os 5 samples, todos os benchmarks — nenhuma referência a API removida
  ou comportamento desatualizado além dos achados acima.

### auditoria-complexidade — concluída

- ✅ **[BAIXA, corrigido imediatamente — não é trade-off, é bug]** Comentário do
  Round 1 em `ProcessTick`'s `LogMessage` afirmava incorretamente que `LogMessage` já
  checava `IsEnabled(Debug)` — na verdade só checava `Logger is null`. Como o Round 1
  passou a chamar `LogMessage` a cada tick do Monitor (~300ms por padrão, pela vida
  inteira de todo buffer elástico, não só por operação de scale como as chamadas
  pré-existentes), isso significava formatar uma string + criar um closure sem
  necessidade sempre que um `Logger` está configurado, mesmo com nível mínimo acima
  de Debug. Corrigido: `!Logger.IsEnabled(LogLevel.Debug)` adicionado ao guard de
  `LogMessage` (mesmo padrão já usado em `RingBufferBuilder.cs`), comentário
  corrigido.
- **[BAIXA-MÉDIA, não medida — encaminhado]** Varredura fresca encontrou um padrão
  pré-existente (não tocado pelo Round 1) de "CTS/timer por requisição" em
  `AcquireCoreAsync` (`RingBufferManager.cs:297-299`) — potencialmente o único
  caminho verdadeiramente quente-por-requisição da biblioteca. `AcquireThroughputBenchmarks.cs`
  já existe e já tem `[MemoryDiagnoser]`, cobrindo exatamente esse caminho —
  encaminhado para `auditoria-desempenho` ler a coluna `Allocated` antes de decidir
  se vale investigar mais. Se virar proposta de fix, precisa de `auditoria-estabilidade`
  primeiro (mexe em cancelamento/timeout).
- H1/H2/H3 revisitados só para confirmar que não foram alterados pelo Round 1 —
  decisões anteriores permanecem válidas, nenhum motivo novo para reabrir.

### auditoria-observabilidade — concluída

- Verificação item a item de todas as 6 mudanças de telemetria do Round 1 (tag
  `target`, tag `trigger` em `scale.duration`, `scaleTrigger` nos logs, 3 logs de
  Debug do `ProcessTick`, `LogWarning` do pin, doc do `OnError`) — **todas corretas**,
  confirmadas lendo o código real ponto a ponto (arquivo:linha citado para cada uma).
  `LogWarning` do pin confirmado disparar se e somente se `!scaledDown &&
  scaleTrigger == "manual"` — "nem mais, nem menos", já que `floor`/`backlog` nunca
  chegam a scale-down.
- Suspeita de parent-chaining de `Activity` reinvestigada de forma independente
  (4 `SwitchToAsync` sequenciais) — refutada de novo, corrobora a conclusão do
  Round 1.
- ✅ **[MÉDIO — mesmo achado do overclaim do log do Monitor acima, corroborado
  independentemente com prova empírica mais forte]** Probe com `Logger` real
  capturando Debug: fase idle capturou 21 linhas "Monitor tick" (prova que o pump
  está vivo); fase saturada (`CurrentCapacity` já em `MaxCapacity`, ~30 ciclos de
  tick em 3s) capturou **zero** linhas novas — falsificação direta e discriminada
  (não "log ausente", mas "log ausente com o pump comprovadamente processando
  ticks"). Também identificou 2 pontos adicionais de "skip" (Tick pulado por
  `_scaling` ativo, e por pin manual ativo) que também nunca passam pelos 3
  `LogMessage` — mesma causa raiz, já coberto pela correção de doc acima.
- ✅ **[BAIXO — corrigido]** Assimetria: os 4 logs de texto livre de scale agora
  interpolam `trigger` mas não `target`, enquanto as métricas/Activity carregam
  ambos. A mesma justificativa do Round 1 para adicionar `trigger` ao texto se aplica
  a `target`.
- ✅ **[BAIXO, pré-existente ao v6 — corrigido por decisão do usuário 2026-08-24]**
  `RingBufferBuilder.cs:333`'s `LogError` sempre passava `null` como o parâmetro
  `Exception?` do `LoggerMessage.Define`, mesmo quando a exceção real está
  disponível — `RingBufferManager.cs:1863`'s `LogError` passa corretamente. Confirmado
  via `git log -L` que a diferença é pré-existente (commit `d10d2ee`, antes do v6
  audit), não regressão do Round 1. Gap real para sinks estruturados (Application
  Insights, Serilog) que dependem do campo `Exception` canônico. Corrigido: passa a
  exceção real. Red/green feito (captura via Moq do argumento `Exception?` real
  passado a `ILogger.Log`). Corrigiu de brinde um warning de nulidade que o próprio
  refactor do guard `IsEnabled` (achado acima) tinha introduzido.
