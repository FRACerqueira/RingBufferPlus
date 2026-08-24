# Plano de ação — auditoria pré-release v6.0.0

Log iteração-por-iteração do que foi feito em resposta aos achados de
`relatorio-auditoria-v6.md`. Granularidade de execução, não de round (isso fica em
`acompanhamento-convergencia-v6.md`).

---

## Round 1 — 2026-08-23/24

Passes das 6 frentes confirmadas disparadas (com retentativa para 5 delas, ver
`relatorio-auditoria-v6.md`). Todas concluídas: 18 achados distintos (1 Crítico,
4 Alto, 8 Médio, 5 Baixo).

**Corrigidos nesta rodada (os 3 priorizados, a pedido do usuário):**

1. **[Crítico, usabilidade] README.txt "undone" claim** — corrigido diretamente
   (fix de doc, sem red/green, texto trocado para bater com `usage-lock-when-scaling.md`/
   ADR010V01). Busca em todo o repo (`grep -i undone`) confirmou que era a única
   ocorrência desatualizada.
2. **[Alto, resiliência] DisposeAsync pode travar para sempre via heartbeat pump** —
   trade-off apresentado ao usuário antes de implementar (reverte parte de uma
   decisão anterior documentada como deliberada); usuário escolheu a opção cirúrgica
   (delimitar só a chamada do pump em `RunHeartbeatAsync`, sem tocar em
   `TurnbackAsync` nem no comportamento de chamadores externos). Red/green: novo teste
   `HeartBeatInvalidate_WhenItemDisposeHangs_ManagerDisposeAsyncStillReturnsPromptly`
   falhou por 2s de timeout (motivo previsto), passou após o fix. Reutiliza o padrão
   já existente (`_pendingHeartbeatDisposals` + grace period de `PulseHeartBeat`) do
   branch de timeout irmão no mesmo método.
3. **[Alto, resiliência] AddRingBuffer\<T\> acopla falhas entre buffers via GetServices** —
   antes de corrigir, confirmei que `usage-dependency-injection.md` documenta
   explicitamente o padrão `IEnumerable<IRingBufferService<T>>` para múltiplos
   buffers do mesmo T, que depende do registro ser singleton "plano" — a correção
   não podia simplesmente trocar tudo para keyed sem quebrar isso. Fix: registro
   passou a ser um singleton keyed (por `buffername`) + um singleton plano que só
   encaminha para o keyed (preserva o `IEnumerable`/"last-wins" documentado sem
   mudança); `RingBufferWarmupHostedService<T>` resolve via `GetKeyedService`, não
   mais enumerando+filtrando. Red/green: novo teste
   `HostedService_StartAsync_ForOneBuffer_DoesNotConstructAnUnrelatedBufferOfTheSameType`
   falhou propagando a exceção do buffer "bad" (motivo previsto), passou após o fix.
   Teste pré-existente (`HostedService_StartAsync_WarmsUpTheNamedBufferWithItsOwnToken`)
   atualizado para registrar via `AddKeyedSingleton`, já que o mecanismo mudou.

Verificado: 177/177 testes net10.0 (era 175, +2 novos), build limpo nos 3 TFMs
(net8.0/9.0/10.0) para toda a solução (lib + 3 TFMs + samples + benchmarks +
gerador de docs). Nada commitado ainda - aguardando decisão do usuário sobre commit.

**Continuação — 2026-08-24, "próximos achados":**

Corrigidos mais 11 achados/instâncias (2 Alto, 5 Médio, 2 Baixo, +2 novas
ocorrências da mesma classe encontradas via grep durante os fixes):

- Doc: `usage-rabbitmq.md` (Fault/Tick incorreto), `overview.md` (AutoScaleDecision
  removida), `usage-observability.md` (link morto + taxonomia de trigger + ADR
  desatualizada), `concepts.md` (ADRs supersedidas), `ADR007V03` (LockWhenScaling
  "stays removed" — falso; usuário confirmou corrigir o texto do ADR diretamente,
  tratando como correção factual, não nova decisão) — todos fixes diretos, sem
  red/green (mudança de texto, não de comportamento).
- Código, com red/green completo (stash temporário da fonte do fix, confirma
  falha pelo motivo certo, restaura, confirma verde): validação de piso para
  `PulseHeartBeat` em `ValidateBuild` (Médio), interpolação de `scaleTrigger` nos
  4 logs de `DispatchScaleUp`/`DispatchScaleDown` (Médio), tag `trigger` em
  `scale.duration` (Baixo).
- Código, sem red/green (robustez teórica, sem cenário de falha reproduzível):
  `_disposed` → `volatile bool`.
- Doc + XML doc, escopo maior que o achado original: `OnError` "substitui, não
  soma" o log do `Logger` — a frase enganosa estava duplicada em 3 interfaces
  (`IRingBufferBuilder`/`IRingBufferFixedBuilder`/`IRingBufferElasticBuilder`), não
  só uma; docs de API regeneradas corretamente (rebuild lib → rebuild gerador →
  gerar, nessa ordem).

Verificado: 180/180 testes net10.0 (era 177, +3 novos), build limpo em toda a
solução (3 TFMs, samples, benchmarks, gerador de docs) a cada etapa.

**Ainda não corrigidos** (aguardando decisão do usuário — ver próxima mensagem):
2 Médio candidatos pendentes de corroboração (pin parcial, fan-out de Tasks),
1 Médio de design (telemetria do Monitor sem contexto de decisão), 2 Baixo de
complexidade (estruturas de dados do algoritmo Monitor, valor questionável nos
defaults atuais), gaps de cobertura de benchmark (recomendação, não um fix de
código existente).

## Round 2 — 2026-08-24

Todas as pendências acima resolvidas nesta rodada (corroborações rodadas, decisões
tomadas — ver `relatorio-auditoria-v6.md` Round 1 para os detalhes). Round 2 em si:
6 frentes fresh, mesmo enquadramento do Round 1, focadas em regressão + achados
novos. 3/6 precisaram de retentativa por infra (mesma falha recorrente de stall/erro
de API já vista no Round 1).

**Achados fechados nesta rodada** (12 total: 2 Alto, 6 Médio, 4 Baixo — ver
`relatorio-auditoria-v6.md` seção Round 2 para os detalhes completos de cada um):

- 2 Alto: guard `IsEnabled` desprotegido (violação do invariante F23) — introduzido
  pela própria correção desta sessão, achado pela estabilidade; `AddHostedService`
  deduplicando por tipo (pré-existente desde ADR007V03, nunca antes pego) — só o 1º
  buffer de cada T recebia warmup automático, achado pela resiliência após ~65min
  de investigação real.
- 2 Médio de código: falha tardia do dispose adiado do heartbeat não observada
  (mesmo padrão `ContinueWith` da F12/F15 aplicado); duas imprecisões de mensagem
  de log corrigidas junto.
- Doc: 4 achados de usabilidade (guias desatualizados face aos fixes do Round 1),
  overclaim do log do Monitor corroborado independentemente por 2 frentes com prova
  empírica, assimetria target/trigger nos logs, e a recomendação de
  `IEnumerable<IRingBufferService<T>>` substituída por `[FromKeyedServices]`
  (verificado empiricamente que funciona por construtor plano em qualquer host).
- 2 achados pré-existentes ao v6 (não regressão desta sessão, mas fechados por
  decisão do usuário): mesma classe do bug do guard `IsEnabled` em
  `RingBufferBuilder.cs`; `LogError` do builder passando `null` em vez da exceção
  real para sinks estruturados.
- Confirmação empírica de desempenho (Baixo, sem regressão): o fix do `LogMessage`
  guard mede ~39x mais rápido no caminho gated, mas impacto absoluto desprezível.
  Benchmark novo (`MonitorTickLogGuardBenchmarks.cs`) mantido no repo.

Verificado: 186/186 testes net10.0 (era 183, +3 novos), build limpo (0 warnings,
0 errors) em toda a solução a cada etapa. Nenhum achado do Round 2 ficou em aberto
sem decisão.

## Round 3 — 2026-08-24

Mesmo enquadramento, focado em regressão do Round 2 + achados novos. Achado mais
importante da rodada: **o fix `SafeIsEnabled` do Round 2 nunca foi de fato conectado**
em `RingBufferManager.cs` — o helper foi criado mas o call site continuava usando
`Logger.IsEnabled` cru. Confirmado de forma **independente por 3 das 6 frentes**
(estabilidade, resiliência, observabilidade), cada uma reproduzindo empiricamente o
mesmo cenário (Logger cujo `IsEnabled` lança derruba `WarmupCoreAsync`). Corrigido
(uma linha) com red/green.

Também corrigidos: 4 achados de doc da usabilidade (ordem de parâmetros
desatualizada em `concepts.md`, redação obsoleta em `usage-dependency-injection.md`,
4ª ramificação faltante no catálogo de mensagens de heartbeat, link cruzado
incorreto). Desempenho fechou a pendência do CTS/timer (Round 2/3 de complexidade)
com número real: 69% da alocação do caminho rápido de `AcquireAsync` é overhead do
CTS/timer nunca consultado. Duas das três causas de alocação nesse trecho corrigidas
com baixo risco (Stopwatch sem alocação, delegate `TurnbackAsync` cacheado) —
medido: 496B, redução real de ~104B/operação. A terceira (CTS/timer preguiçoso) foi
decidida como "manter como está", documentada como comentário no código (mesmo
padrão do trade-off "4x amplification" já usado no projeto — não é escala de ADR).

Verificado: 187/187 testes net10.0 (era 186, +1 novo), build limpo (0 warnings,
0 errors) em toda a solução a cada etapa. Nenhum achado do Round 3 ficou em aberto
sem decisão.

## Round 4 — 2026-08-24

Mesmo enquadramento, com atenção redobrada aos fixes "confirmados" de rodadas
anteriores (Rounds 2 e 3 tiveram cada um um caso de fix incompleto não pego por
testes/build). Todos os 4 fixes do Round 3 confirmados de fato conectados por
pelo menos 2 frentes independentes — sem repetição do padrão.

**Corrigidos nesta rodada** (2 Alto, 2 Médio, 1 Baixo, 1 achado de complexidade
não medido virado fix):

- Doc: `README.txt` (Alto) — "What's new" ainda afirmava v5.0.0 como versão
  atual e uma exclusividade `AutoScaleAcquireFault`/`SwitchToAsync` removida
  inteiramente em v6; miss do próprio Round 3. `ADR007V03`/`usage-observability.md`
  (Alto) — alinhados à semântica de substituição `Logger`/`OnError` já decidida
  e fixada nos XML docs no Round 1, nunca propagada a esses dois documentos.
  `usage-observability.md` (Baixo) — números de overhead de listener re-medidos
  (`ObservabilityOverheadBenchmarks -i`: 592B/1216B → 544B/1168B, refletindo o
  fix de alocação do Round 3).
- Código, com red/green completo: TOCTOU de identidade de exceção (Médio) entre
  o guard `ObjectDisposedException.ThrowIf(_disposed, this)` e o acesso
  subsequente a `_lifetime.Token` em `AcquireCoreAsync`/`SwitchToAsync`/
  `WarmupCoreAsync` — corrigido com um helper `LifetimeToken()` que normaliza a
  identidade da exceção nos 6 pontos de acesso vulneráveis. Red/green: reprodução
  determinística via reflection do contrato do helper (a janela de corrida real
  não é forçável deterministicamente sem instrumentar produção — documentado
  explicitamente no teste) + validação empírica probabilística descartável
  (3842 hits pós-fix, 0 identidade errada; pré-fix, 1 identidade errada em volume
  comparável). Assimetria de tags (Médio) em `ringbufferplus.acquire.duration` —
  caminho de sucesso não emitia `acquire.timed_out`/`acquire.cancelled`, violando
  o próprio princípio de tag contract já declarado para `scale.*`; corrigido,
  red/green feito estendendo um teste existente.
- Código, seguindo diretamente da complexidade (H-A, não era achado formal, mas
  virou fix de baixo risco): boxing de `bool` nas tags de métrica do caminho de
  sucesso de `AcquireCoreAsync`, agravado pelo próprio fix de tag symmetry acima
  (+48B/op medido). Corrigido com 2 campos estáticos cacheados
  (`BoxedTrue`/`BoxedFalse`), escopo restrito ao caminho medido. Medido:
  544B → 472B — líquido abaixo dos 496B do Round 3, apesar das 2 tags novas.

**Documentado como limitação conhecida, não corrigido** (mesmo formato do
trade-off CTS/timer do Round 3 — comentário no código, não ADR): janela
construtor-vs-inicializador-de-objeto em `RingBufferManager` que pode expor
`buffer.name: null` a um `MeterListener` que colete durante a construção
(Baixo, instância única).

**Sem ação** (achados frios/descartados, achados que se limitam a uma nuance
textual sem impacto funcional): imprecisão na justificativa do commit `df50203`
sobre "paridade" de `LogError` (Baixo, textual); H-B (`EngineCommand` alocado
fora do caminho quente); H-C (`RingBufferValue<T>` bloqueado de virar `struct`
por correção, não por desempenho).

Verificado: 188/188 testes net10.0 (era 187, +1 novo), build limpo (0 warnings,
0 errors) em toda a solução a cada etapa. Nenhum achado do Round 4 ficou em
aberto sem decisão.

## Round 5 — 2026-08-24

Mesmo enquadramento. Estabilidade e resiliência não encontraram nada novo além
do já mapeado (`LifetimeToken()` do Round 4 confirmado conectado e correto por
ambas, uma via reprodução empírica de 1600 hits, outra via >26.000 tentativas
sem pousar na janela real — corroborando, não contradizendo, o fix). Os outros
4 ângulos encontraram achados, nenhum deles bug de comportamento novo — todos
de doc ou de tag de telemetria seguindo exatamente o mesmo padrão já corrigido
no Round 4.

**Corrigidos nesta rodada** (2 Alto, 2 Médio):

- Doc: `CONTRIBUTING.md` (Alto) — 3 referências a `ADR004V02`/`ADR006V01`
  (superseded) e uma afirmação falsa de que a exceção de v5.0.0 ao ciclo de
  deprecação não se repetiria — `ADR004V03` já autoriza um 2º reset para
  v6.0.0. 3ª recorrência da mesma classe de bug no mesmo arquivo (já fechada
  uma vez na auditoria v5.1, achado U-27, para uma versão de ADR anterior).
  `CHANGELOG.md` (Médio) — mesma causa raiz, o parágrafo de enquadramento no
  topo do arquivo nunca foi atualizado quando a seção Unreleased já tinha sido.
  `usage-observability.md` (Baixo, achado da própria frente de desempenho) —
  números 544B/1168B do Round 4 já estavam stale (medidos antes do fix de
  boxing daquele mesmo round); re-medido nesta sessão: 472B/1048B, e o
  parágrafo de comparação com o baseline histórico reescrito (a comparação
  "before vs. after" não é mais válida com esses números).
- Código, com red/green completo: Activity `"RingBufferPlus.Acquire"` tinha a
  mesma assimetria de tag-set que foi o achado principal do Round 4 para a
  métrica `acquire.duration` — a tag `cancelled` só era setada no catch de
  cancelamento do chamador, ausente (não `false`) nas outras 2 linhas.
  Corrigido adicionando `cancelled=false` às 2 linhas que faltavam; doc
  reescrita para descrever a tag como sempre presente.

**Candidato não medido, sem ação** (H4, encaminhado por complexidade): cada
`AcquireAsync` elástico em espera grava um `EngineCommand.Backlog()` sem
coalescência no canal único-consumidor — sob backlog sustentado com muitos
esperadores, risco de enfileiramento atrás de comandos de estado real. Não
medido; requer aprovação de estabilidade antes de qualquer mitigação, dado o
histórico do projeto nessa área.

Verificado: 188/188 testes net10.0 (mesmo total do Round 4 — as 2 novas
asserções estenderam testes existentes, não criaram novos `[Fact]`), build
limpo (0 warnings, 0 errors) em toda a solução a cada etapa. Nenhum achado do
Round 5 ficou em aberto sem decisão (H4 é explicitamente um candidato não
medido, não um achado pendente).

## Round 6 — 2026-08-24

Enquadramento reduzido para estabilidade/resiliência (1 rodada limpa cada no
Round 5), completo para as outras 4 frentes.

**Corrigidos nesta rodada** (1 Alto, 1 Médio, 3 Baixo, mais 2 achados de ADR
fora do escopo original das 6 frentes):

- **[Médio, observabilidade, O9] Activity `"RingBufferPlus.Scale"` sem tag
  `success`** — `scale.operations`/`scale.duration` já carregavam `success`;
  a Activity nunca ganhou o `SetTag` correspondente em `DispatchScaleUp` nem
  `DispatchScaleDown`. 3ª instância da classe já corrigida para Acquire nos
  Rounds 4-5. Red/green: novo teste
  `ScaleActivities_CarrySuccessTag_MatchingTheScaleOperationsMetric` falhou
  com `Expected: True, Actual: null` (motivo previsto), passou após adicionar
  `activity?.SetTag("success", scaledUp/scaledDown)` nos dois métodos.
  Varredura de todos os `activity?.SetTag` do arquivo confirmou paridade total
  com as métricas irmãs — nenhuma outra ocorrência.
- **[Alto, observabilidade, O10] Doc afirma falsamente que Scale correlaciona
  com o trace do chamador** — `usage-observability.md:35` dizia que Acquire e
  Scale "both correlate naturally with the rest of your request trace".
  Falso para Scale: `DispatchScaleUp`/`DispatchScaleDown` rodam no loop do
  engine, nunca no fluxo async de um chamador, então toda Activity Scale é
  sempre raiz de trace. Doc-only (propagar contexto do chamador seria uma
  decisão de design nova, não um bug a reparar — não há um único "chamador"
  para `auto`/`floor`/`backlog`).
- **[Baixo, observabilidade, O11] Log "Stopped Heart Beat item" enganoso** —
  implicava conclusão mesmo quando o callback ainda está rodando e o dispose
  foi deferido. Reescrito para "Heart Beat pump iteration finished". Sem
  teste (textual; grep confirmou que nenhum teste depende da string antiga).
- **[Baixo, observabilidade]** Strings de `description` dos instrumentos
  `scale.operations`/`scale.duration` (metadata OTel) omitiam
  `buffer.name`/`target`/`cancelled` — corrigido (mecânico).
- **[Baixo, observabilidade]** `usage-observability.md:29` — wording "mirrors"
  ambíguo entre `acquire.cancelled` (métrica, prefixado) e `cancelled`
  (Activity, sem prefixo) — clarificado.
- **[Alto, usabilidade]** `doc/architecture/v6-design-proposal.md` nunca
  aberto em 5 rounds apesar de referenciado por `ADR006V02:24` (ADR
  atualmente Aceito) e conter versões de ADR desatualizadas — banner
  "SUPERSEDED" adicionado (não deleção, para não quebrar a referência do
  ADR006V02).
- **Fora do escopo das 6 frentes** (achado durante a limpeza de
  descontinuação da v5.1.0 pedida pelo usuário no meio da rodada):
  `ADR004V03` (Aceito, não histórico) ainda decidia "a próxima release é
  5.1.0" e tratava v5.1.0 como release passada real que v6.0.0 sucederia.
  Como nenhuma 5.1.0 chegou a ser publicada, o usuário confirmou
  explicitamente que corrigir o próprio ADR004V03 in-place (seguindo seu
  próprio padrão interno de "Amended on DATE", sem nova versão via
  `adrplus`) não fere a convenção de imutabilidade deste projeto — essa
  protege decisões que de fato entraram em vigor, e esta nunca entrou.
  `ADR003V03` tinha uma citação órfã para um trecho do `CHANGELOG.md` já
  apagado nessa mesma limpeza ("sample window resets across a scale
  operation...") — corrigida removendo só a citação, sem alterar a alegação
  de fundo (comportamento real, ainda implementado). `ADR006V02` conferido,
  sem necessidade de alteração.

**Investigado e refutado, sem fix** (estabilidade): suspeita de leak de
`Activity.Current` entre operações de Scale consecutivas. Mecanismo
verdadeiro em isolamento (uma reprodução standalone síncrona mostrou o leak),
mas não reproduz no `RingBufferManager` real — `AsyncMethodBuilderCore.Start`
salva/restaura o `ExecutionContext` em torno de `await ProcessCommandAsync`,
revertendo a mutação antes que ela escape do loop do engine, mesmo em um
caminho (Switch) que nunca chega a dar `await`. 3ª vez que uma suspeita desse
tipo específico é levantada e refutada nesta série (Rounds 1, 3, 6). O teste
de reprodução (`ConsecutiveScaleOperations_ProduceIndependentRootActivities_NotChainedToEachOther`)
foi mantido como guarda de regressão permanente, com o comentário reescrito
para descrever o mecanismo que previne o bug, e reforçado com asserções
adicionais (`Parent`/`ParentSpanId`).

**Fechado por análise estática, sem medição** (H4, complexidade + desempenho,
2 argumentos independentes): o gate `if (!Elastic || _scaling ||
CurrentCapacity >= MaxCapacity) return;` em `EvaluateBacklogReactive` já torna
qualquer sinal de backlog redundante barato o suficiente — coalescência
adicional não se paga.

**Correção de processo** (desempenho, 2ª vez seguida): os números
re-medidos no Round 5 (472B/1048B) já estavam stale — medidos antes do fix
`cancelled`-tag daquele mesmo round ter sido aplicado. Desta vez, todo o
código do Round 6 (O9 incluído) foi aplicado primeiro, seguido de rebuild
limpo (3 TFMs, 0 warnings) e uma única medição real depois de tudo pronto
(`dotnet run -c Release --project benchmarks/RingBufferPlus.Benchmarks -f
net10.0 -- -i --filter "*ObservabilityOverheadBenchmarks*"`). Número real:
472 B sem listener (estável), **1088 B** com listener (não 1048 B) — ~2.3x;
tempo ~307ns/~593ns (~1.9x), reescrito como ordem de grandeza em vez de valor
pontual preciso.

Verificado: 190/190 testes net10.0 (era 188, +2 novos —
`ScaleActivities_CarrySuccessTag_MatchingTheScaleOperationsMetric` e
`ConsecutiveScaleOperations_ProduceIndependentRootActivities_NotChainedToEachOther`),
build limpo (0 warnings, 0 errors) em toda a solução (3 TFMs, samples,
benchmarks, gerador de docs). Nenhum achado do Round 6 ficou em aberto sem
decisão.

## Round 7 — 2026-08-24

Estabilidade e resiliência não disparadas (convergência formal atingida no
Round 6). Outras 4 frentes com enquadramento completo.

**Corrigidos nesta rodada** (1 Alto, 1 Baixo de comportamento + 4 Baixo de
doc):

- **[Alto, observabilidade] Blackout de telemetria após falha de warmup
  cacheada** — `EnsureWarmupAsync()`'s `Lazy<Task>` cacheia a falha inicial
  (ADR011); toda chamada implícita subsequente de `AcquireAsync`/
  `SwitchToAsync` relançava a mesma exceção antes de `_activitySource.
  StartActivity` rodar, sem deixar span, métrica ou log algum. Apresentei 3
  opções ao usuário (A: métrica/trace sem repetir log; B: métrica/trace +
  log a cada chamada; C: só documentar) — **escolhida A**. Red/green: novo
  teste `AcquireAsync_AfterWarmupFailureIsCached_StillRecordsDurationAndActivity`
  falhou com coleção de `acquire.duration` vazia (motivo previsto), passou
  após envolver `EnsureWarmupAsync()` num try/catch que emite Activity +
  métrica (`success=false`/`timed_out=false`/`cancelled=false`, status
  `Error`) sem chamar `LogError` de novo.
- **[Baixo, observabilidade] Heartbeat inflava `_waitingCount`** — só o
  disparo do `EngineCommand.Backlog()` era gateado por
  `countsTowardFaultBudget`; o `Interlocked.Increment(ref _waitingCount)`
  rodava sempre, inflando o contador que `EvaluateBacklogReactive`/
  `ProcessTick` leem para calcular o alvo de scale-up (efeito real: no
  máximo +1, autoatenuado). Apresentei 3 opções (A: só corrigir a prosa; B:
  corrigir o comportamento; C: descartar) — **escolhida B**. Red/green: novo
  teste `HeartbeatAcquireWaiting_DoesNotInflateWaitingCount` (via reflection
  sobre `AcquireForHeartbeatAsync`/`_waitingCount`) falhou com contador=1
  (motivo previsto), passou após gatear o incremento/decremento por
  `countsTowardFaultBudget`.
- **[Baixo × 4, doc]** 3 citações de rounds internos de auditoria vazando
  para guias públicos (`usage-rabbitmq.md`, `usage-dependency-injection.md`,
  `usage-observability.md`) reescritas para serem autocontidas; resíduo na
  seção Links do `ADR004V03` (ainda descrevia v5.1.0 como release real,
  incluindo referência órfã aos arquivos de `TODO/` já deletados) corrigido;
  `usage-observability.md:50` "~1.9x" corrigido para "~2x" (desempenho mediu
  ~1.98x); números indicativos do Round 1 (`MonitorTickCostBenchmarks`,
  `ScaleRejectionCostBenchmarks`, `ElasticAcquireUnderBacklogBenchmarks`)
  atualizados com medição em modo completo, sem mudança de conclusão.

**Candidato roteado, deferido** (H-D, complexidade, não medido):
`_pendingHeartbeatDisposals` (`ConcurrentBag<Task>`) sem afinidade de thread
real dado o padrão de acesso do pump do heartbeat — complexidade pediu
revisão de estabilidade antes de qualquer troca estrutural (mecanismo que
fecha F12/F15). **Decisão do usuário: deferir para o Round 8** (quando
estabilidade rodar de novo), não medir/alterar agora.

**Resolvido, sem ação**: preocupação de proveniência sobre comentários
"Round 7, Resiliência/Estabilidade" já existentes no código — confirmado via
`git blame` que são de 2026-08-21, de uma série de auditoria anterior e não
relacionada (pré-v6), coincidência de numeração, não vazamento desta rodada.

Verificado: 192/192 testes net10.0 (era 190, +2 novos), build limpo (0
warnings, 0 errors) em toda a solução (3 TFMs, samples, benchmarks, gerador
de docs). Nenhum achado do Round 7 ficou em aberto sem decisão (H-D é
explicitamente um candidato deferido, não um achado pendente).

## Round 8 — 2026-08-24

4 frentes completas (usabilidade, complexidade, desempenho, observabilidade)
+ estabilidade em escopo pontual (só o candidato H-D roteado no Round 7).
Resiliência não disparada.

**Corrigidos nesta rodada** (1 Alto de comportamento, 1 candidato de
complexidade implementado, 1 achado Médio refutado):

- **[Alto, observabilidade] Tag `acquire.warmup_failed` adicionada** — a
  linha nova de falha de warmup cacheada (fix do Round 7) era idêntica em
  tags a um shutdown comum, anulando o propósito do próprio fix do Round 7
  para quem só usa métricas. Apresentei 3 opções (A: nova tag; B: estender
  telemetria a `SwitchToAsync` também; C: não mexer) — **escolhida A**.
  Red/green: 4 testes de desfecho de `AcquireAsync` estendidos com a nova
  tag (`false` nos 3 desfechos existentes, `true` só no novo). Resolve
  também o achado equivalente de usabilidade na doc (invariante
  `Error ⇔ timed_out=true` quebrada). `SwitchToAsync`'s lacuna (nunca
  recebeu telemetria própria por chamada, nem antes nem depois do Round 7)
  documentada como limitação conhecida, não corrigida em código.
- **[H-D, complexidade→estabilidade] `ConcurrentBag` → `lock`+`List<Task>`
  implementado** — estabilidade revisou e liberou como seguro (nenhum
  outro escritor além de `RunHeartbeatAsync`, garantias de correção vêm do
  `await _heartbeatTask`, não do tipo da coleção), com 2 recomendações de
  implementação seguidas (lock também na leitura; `RemoveAll` em vez do
  padrão take/re-add) e um bônus: fecha um hazard latente de perda de
  entrada sob exceção entre os loops de drenagem/reinserção do padrão
  antigo. Decisão do usuário: implementar agora, sem esperar desempenho
  medir o ganho (dado o baixo risco já confirmado e o bônus de correção).
- **[Médio, observabilidade, investigado e REFUTADO]** suspeita de que
  `SwitchToAsync` manual com `LockWhenScaling=false` nunca logava uma
  falha genuína de factory — refutado por reprodução empírica (4 chamadas
  de `LogError`, uma por tentativa concorrente, já fazendo isso via o
  `catch` por tentativa dentro de `CreateItemsAsync`, independente de
  quem/se alguém espera o resultado do batch). Teste mantido como guarda
  de regressão permanente.

**Achado de desempenho, sem fix nesta rodada** [Baixo]: o
`Stopwatch.GetTimestamp()` extra do fix do Round 7 custa +20 a +24ns
(+7-8%) no caminho comum de `AcquireAsync`, medido com A/B contra o commit
anterior ao Round 7. Decisão do usuário: **aceitar como está** (opção 1A
de 3 apresentadas) — negligível frente a qualquer I/O real de Factory.

**Sem achado após investigação**: candidato de complexidade sobre o
padrão "drenar e reconstruir" de `_pendingHeartbeatDisposals` (O(n) por
inserção) — a própria frente rebaixou a "registrado, sem ação" dado que a
taxa de chegada é limitada pela cadência do heartbeat (mesmo enquadramento
de descarte já usado para H1/H2).

Verificado: 193/193 testes net10.0 (era 192, +1 novo), build limpo (0
warnings, 0 errors) em toda a solução (3 TFMs, samples, benchmarks, gerador
de docs). Nenhum achado do Round 8 ficou em aberto sem decisão.

## Round 9 — 2026-08-24

Enquadramento ajustado por pilar após análise de tendência de 8 rounds:
complexidade reduzida, usabilidade completa, desempenho redefinido como
verificação de regressão ligada a mudanças de código (registrado também em
`C:\Sources\EA4AI\agents\auditoria-desempenho.md`, compartilhado entre
projetos), observabilidade em formato de checklist único em vez de mais
uma rodada incremental. Estabilidade/resiliência não disparadas.

**Corrigido nesta rodada** (1 Médio):

- **[Médio, observabilidade] Contador `ringbufferplus.heartbeat.invalidations`
  adicionado** — o veredito "não saudável" do `HeartBeat` (`Invalidate()`
  + substituição, o modo de operação mais comum do recurso) não gerava
  nenhum sinal distinto de um pulso saudável em log/métrica/trace.
  Apresentei 4 opções (A: log Debug; B: contador dedicado; C: os dois; D:
  só documentar) — **escolhida C**. Comecei a implementar antes do
  resultado de desempenho voltar (área de código sem overlap com o que
  estava sendo medido - confirmado com o usuário antes de prosseguir).
  Red/green: novo teste `HeartBeat_UnhealthyVerdict_RecordsInvalidationCounter`
  falhou com 0 registros (motivo previsto), passou após adicionar o
  contador + log logo após `Invalidate()`.

**Sem achado, rodadas genuinamente limpas** (1ª vez para as duas nesta
série):
- Complexidade: confirmou a troca do `ConcurrentBag` do Round 8 sem
  candidato novo; passada fresca sem achado.
- Usabilidade: passada completa sem achado (1 typo cosmético fora do
  escopo formal, não reportado como achado).

**Medido, sem trade-off a decidir** (desempenho):
- `ConcurrentBag`→`lock`+`List<Task>` (Round 8): confirmado como ganho
  puro (~3-5x mais rápido, 0B de alocação vs. 32-176B/pulso) - não é
  regressão, nada para escalar.
- Tag `acquire.warmup_failed` (Round 8): +40B/+39ns mensurável, mas
  confinado ao caminho com listener anexado; caminho sem listener
  (o caso comum) ficou inalterado. Não recomendado para escalar.
- Desempenho corrigiu a própria metodologia no meio do trabalho (baseline
  errado na 1ª tentativa; microbenchmark inicial media construção de
  coleção em vez de poda) antes de reportar os números finais.

Verificado: 194/194 testes net10.0 (era 193, +1 novo), build limpo (0
warnings, 0 errors) em toda a solução (3 TFMs, samples, benchmarks, gerador
de docs). Nenhum achado do Round 9 ficou em aberto sem decisão.
