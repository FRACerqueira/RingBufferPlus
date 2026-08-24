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
