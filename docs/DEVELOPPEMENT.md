# Développement

## Organisation

| Dossier | Contenu |
|---|---|
| `src/QuaiMail.Core` | sans interface : mises à jour (`Updates/`), réglages et fichiers (`Storage/`), brouillons locaux (`Drafts/`), état de la fenêtre (`Models/WindowState.cs`, `Storage/WindowStateStore.cs`), view-models (`ViewModels/`) |
| `src/QuaiMail.App` | WinUI 3 : fenêtre et ses trois volets, bandeau de mise à jour, dialogues, réglages, installation des mises à jour (`Services/UpdateInstaller.cs`), test de fumée (`SmokeTest.cs`), gabarit de lecture des mails (`Assets/Mail`) |
| `tests/QuaiMail.Core.Tests` | xUnit : mises à jour (source GitHub, comparaison des versions, téléchargement vérifié, bandeau), réglages, brouillons, état de la fenêtre, clés de traduction |
| `installer/QuaiMail.iss` | installateur Inno Setup (par utilisateur, sans droits administrateur) |
| `tools/make_icon.py` | génère l'icône (Pillow) |

`QuaiMail.Core` ne dépend pas de Windows : ses tests tournent aussi sous Linux.

## Commandes

```
dotnet test --project tests/QuaiMail.Core.Tests/QuaiMail.Core.Tests.csproj
dotnet build src/QuaiMail.App/QuaiMail.App.csproj -p:Platform=x64        (Windows seulement)
dotnet publish src/QuaiMail.App/QuaiMail.App.csproj -c Release -r win-x64 -p:Platform=x64 -o publish/x64
publish\x64\QuaiMail.exe --smoke-test %TEMP%\quaimail-smoke
python tools/make_icon.py src/QuaiMail.App/Assets/QuaiMail.ico src/QuaiMail.App/Assets/AppIcon.png docs/images/logo.png
```

La variable d'environnement `QUAI_MAIL_DATA_DIR` redirige toutes les données (réglages, brouillons, journaux) : les tests de fumée s'en servent pour ne jamais toucher au vrai profil.

## Arguments de la ligne de commande

| Argument | Rôle |
|---|---|
| `--smoke-test <dossier>` | test de fumée du CI : réglages, thème sombre, brouillon simulé et « Garder ce brouillon ? », état de la fenêtre noté puis restauré, bandeau de mise à jour (release fictive, sans réseau) ; captures et rapport dans le dossier, code 0 ou 1 |
| `--update-test <dossier> <installateur>` | mise à jour en place du CI : l'installateur local est présenté comme une release plus récente (vraie taille, vraie empreinte), un brouillon et un état de fenêtre sont simulés ; Quai Mail télécharge, vérifie, garde le brouillon, lance l'installateur et se ferme |
| `--after-update` | passé par l'installateur quand il relance Quai Mail : restaure l'état noté dans `updates/reopen.txt`, une fois |

## Chaîne de mise à jour

1. `UpdateBannerViewModel.CheckAsync` → `UpdateChecker` → `GitHubReleaseSource` (`releases/latest`, sans jeton) : version SemVer plus récente, installateur de l'architecture (`QuaiMail-Setup-x64.exe` / `-arm64.exe`) avec son `digest` SHA-256.
2. Clic sur **Mettre à jour** : `UpdateDownloader` télécharge dans `updates/` et vérifie taille et SHA-256.
3. `UpdateInstaller.InstallAndRestartAsync` → `ShellViewModel.PrepareForUpdateAsync` : « Garder ce brouillon ? », puis `WindowStateStore.SaveForUpdate` écrit `updates/reopen.txt` ; l'installateur est lancé avec `/SILENT /RELAUNCH=1`, Quai Mail se ferme.
4. L'installateur relance `QuaiMail.exe --after-update` → `ShellViewModel.RestoreAfterUpdate` lit puis efface `reopen.txt`, rouvre le brouillon.

Le CI rejoue toute la chaîne à chaque run (étape « Mise à jour en place ») et cherche dans le journal de la version relancée : « Démarrage juste après une mise à jour », « Brouillon rouvert après la mise à jour », « État de la fenêtre restauré après la mise à jour ».

## Ajouter un texte à l'interface

Toutes les chaînes sont dans `src/QuaiMail.App/Strings/fr-FR/Resources.resw`, utilisées par `{l:Str Key=…}` dans le XAML, `Loc.Get` dans l'application et `IStrings` dans les view-models. Un test vérifie que chaque clé utilisée existe.

## Styles

XAML : tous les styles dans `src/QuaiMail.App/Styles/Common.xaml`, pas de mise en forme en ligne répétée. Lecture des mails : le gabarit `Assets/Mail/reader.html` ne contient ni style ni script, ses styles sont dans `Assets/Mail/mail.css`. Le CI refuse tout HTML ou JavaScript hors de `Assets/Mail`, et toute balise `<style>` ou attribut `style=` dans le gabarit.

## Publier une version

Monter `<Version>` dans `Directory.Build.props` et pousser sur `main` : le CI construit, teste, installe, lance le test de fumée et la mise à jour en place, désinstalle, puis crée le tag `vX.Y.Z` et publie la release (installateurs et zips x64 / arm64, empreintes SHA-256). Une branche de développement construit et teste sans publier.
