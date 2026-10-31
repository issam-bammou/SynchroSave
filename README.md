# SynchroSave

Application Windows qui **sauvegarde automatiquement les sauvegardes de vos jeux PC dans votre OneDrive**
et permet de **les récupérer en quelques clics sur un autre PC**.

## Fonctionnalités

- **Détection automatique** en deux temps :
  - une base de ~70 jeux connus (Elden Ring, Skyrim, Witcher 3, Cyberpunk 2077, Baldur's Gate 3, EA SPORTS FC 24–27,
    Stardew Valley, Hollow Knight, Minecraft, RDR2, Hogwarts Legacy…) ;
  - une **analyse des dossiers** `AppData\Local`, `Roaming`, `LocalLow`, `Documents`, `My Games` et `Saved Games` qui repère
    les sauvegardes des jeux absents de la base : schéma Unreal Engine `<Jeu>\Saved\SaveGames`, dossiers `Saves`, `SaveData`…,
    fichiers `.sav`, `.sl2`, `.ess`, `.es3`… Les dossiers trouvés sont proposés dans une liste à cocher (nom modifiable).
  - et l'ajout de **n'importe quel dossier** à la main.
- **Synchro automatique PC → OneDrive** avec une **fréquence réglable** (de 1 minute à 24 heures, raccourcis 5 min / 15 min / 30 min / 1 h / 6 h / 24 h).
- **Synchro dès qu'une sauvegarde change** (après un délai réglable sans nouvelle écriture, pour attendre la fin de la sauvegarde du jeu).
- **Restauration sur un nouveau PC** : l'onglet « Restaurer depuis OneDrive » liste toutes les sauvegardes présentes dans OneDrive
  (jeu, date, PC d'origine, taille). Cochez celles que vous voulez, l'application propose le bon dossier sur ce PC
  (les chemins sont stockés de façon portable : `{Documents}`, `{AppData}`, `{SavedGames}`…), puis cliquez sur « Restaurer la sélection ».
- **Historique des versions** dans OneDrive (nombre réglable, 10 par défaut) : restaurez une ancienne sauvegarde si une partie est corrompue.
- **Copie de sécurité locale** (zip) avant toute restauration ou écrasement d'un dossier local.
- Deux modes :
  - **Sauvegarde** (par défaut) : ce PC envoie vers OneDrive ; il n'écrase jamais une version plus récente envoyée par un autre PC.
  - **Bidirectionnel** : la version la plus récente est récupérée automatiquement sur chaque PC ; en cas de conflit la plus récente gagne et l'autre est archivée.
- Protection : un dossier local vide n'efface jamais la copie OneDrive ; les fichiers ouverts par un jeu sont lus en partage et réessayés plus tard en cas d'erreur.
- **Icône dans la zone de notification** (synchroniser, pause, quitter), notifications, **lancement au démarrage de Windows**, journal détaillé.
- Fichiers ignorés configurables (`*.tmp;*.lock;…`).

## Comment ça marche avec OneDrive

SynchroSave copie les sauvegardes dans le dossier OneDrive de votre PC (`C:\Users\<vous>\OneDrive\SynchroSave`),
et le client OneDrive intégré à Windows les envoie dans votre compte. Aucune connexion/mot de passe à donner à l'application.

```
OneDrive\SynchroSave\Games\<jeu>\manifest.json   description de la dernière version (date, PC, fichiers)
OneDrive\SynchroSave\Games\<jeu>\Current\        dernière version des fichiers
OneDrive\SynchroSave\Games\<jeu>\Versions\*.zip  historique
```

> Sur le nouveau PC, connectez d'abord OneDrive avec le même compte et laissez-le se synchroniser.
> Si les « Fichiers à la demande » sont activés, OneDrive télécharge les fichiers au moment de la restauration.

## Installation

1. Téléchargez `SynchroSave.exe` (onglet **Actions** du dépôt GitHub → dernier build → artefact `SynchroSave-win-x64`,
   ou page **Releases** si un tag `v*` a été créé). C'est un exécutable unique : rien d'autre à installer.
2. Placez-le par exemple dans `C:\Programmes\SynchroSave\` (ou `%LOCALAPPDATA%\SynchroSave\`) et lancez-le.
3. Au premier lancement : le dossier OneDrive est détecté, les jeux installés sont détectés et cochés,
   et l'application s'ajoute au démarrage de Windows (désactivable dans Paramètres).

Windows SmartScreen peut afficher un avertissement car l'exécutable n'est pas signé : « Informations complémentaires » → « Exécuter quand même ».

### Compiler soi-même

Avec le [SDK .NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) :

```powershell
./build.ps1          # tests + publish\SynchroSave.exe
```

## Utilisation

| Onglet | Rôle |
|---|---|
| **Mes jeux** | Jeux suivis sur ce PC. Cochez/décochez, détectez, ajoutez un dossier, synchronisez maintenant. |
| **Restaurer depuis OneDrive** | Liste des sauvegardes dans OneDrive, choix multiple, destination modifiable, restauration de la dernière version ou d'une version précise, suppression. |
| **Paramètres** | Dossier OneDrive, fréquence, synchro à la modification, mode, nombre de versions, démarrage avec Windows… |
| **Journal** | Historique détaillé de ce qui a été fait. |

### Lancement automatique au démarrage du PC

Activé par défaut dès le premier lancement : SynchroSave démarre tout seul, réduit près de l'horloge, à chaque ouverture
de votre session Windows (aucun droit administrateur requis). Réglable dans **Paramètres** ou par clic droit sur l'icône →
« Lancer au démarrage de Windows ». L'onglet Paramètres indique si c'est réellement actif, y compris si le lancement a été
désactivé dans le Gestionnaire des tâches (onglet Démarrage). Si vous déplacez le `.exe`, lancez-le une fois depuis son
nouvel emplacement : l'entrée de démarrage est mise à jour automatiquement.

Fermer la fenêtre ne quitte pas l'application : elle continue en arrière-plan (clic droit sur l'icône → Quitter).

### Ajouter des jeux à la base de détection

Créez `%LOCALAPPDATA%\SynchroSave\games.custom.json` :

```json
[
  { "id": "mon-jeu", "name": "Mon Jeu", "paths": ["{Documents}\\Mon Jeu\\Saves", "{LocalAppData}\\MonJeu\\Saved"] }
]
```

Jetons disponibles : `{UserProfile}`, `{Documents}`, `{AppData}`, `{LocalAppData}`, `{LocalLow}`, `{SavedGames}`,
`{ProgramData}`, `{PublicDocuments}`, `{ProgramFiles}`, `{ProgramFilesX86}`, `{Steam}`, les variables `%VAR%`, et `*` dans un segment.

## Données de l'application

`%LOCALAPPDATA%\SynchroSave\` : `settings.json`, `state\` (dernier état synchronisé par jeu), `LocalBackups\` (copies de sécurité), `logs\`.

## Structure du code

- `src/SynchroSave.Core` : moteur (détection, synchro, versions, restauration) — .NET 8, multiplateforme, testé.
- `src/SynchroSave.App` : interface WPF Windows + icône de notification.
- `tests/SynchroSave.Tests` : tests unitaires (simulation de deux PC partageant un OneDrive).
