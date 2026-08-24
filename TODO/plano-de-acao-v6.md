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
