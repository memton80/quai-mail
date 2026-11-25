# Connexion à Gmail et Outlook.com par le navigateur (OAuth2) : inscription de Quai Mail

> **À venir.** Depuis la version 0.6, le code de cette connexion est dans Quai Mail (testé contre un fournisseur simulé) mais pas encore proposé : les deux boutons sont grisés « à venir ». Pour l'activer dans une version future : faire les inscriptions ci-dessous, créer les trois secrets GitHub, puis passer `BrowserSignIn.Available` à `true` (`src/QuaiMail.App/Services/BrowserSignIn.cs`).

Gmail et Outlook.com refusent (ou refuseront) les mots de passe en IMAP / SMTP : ils exigent une connexion « OAuth2 ». L'utilisateur se connecte dans **son navigateur**, sur la page de Google ou de Microsoft ; Quai Mail ne voit jamais son mot de passe et reçoit seulement une autorisation, gardée dans le coffre de Windows.

Pour cela, Quai Mail doit être **inscrit** chez Google et chez Microsoft. C'est fait une fois, avec les comptes du propriétaire du projet (Alexandre). Cette page dit exactement quoi cliquer. Rien de payant n'est nécessaire.

À la fin, trois valeurs sont à ranger dans les **secrets du dépôt GitHub** (jamais dans le code) : le CI les intègre à la compilation (variables d'environnement `QuaiMailGoogleClientId`, `QuaiMailGoogleClientSecret`, `QuaiMailMicrosoftClientId` lues par MSBuild ; les mêmes servent pour une compilation locale). Sans elles, Quai Mail fonctionne, simplement sans les boutons de connexion par le navigateur.

| Secret GitHub | Où le trouver |
|---|---|
| `QUAI_MAIL_GOOGLE_CLIENT_ID` | Google, étape 4 |
| `QUAI_MAIL_GOOGLE_CLIENT_SECRET` | Google, étape 4 (pour une application de bureau, Google ne le considère pas comme secret, mais il reste hors du dépôt) |
| `QUAI_MAIL_MICROSOFT_CLIENT_ID` | Microsoft, étape 2 |

Dans GitHub : dépôt `memton80/quai-mail`, **Settings**, **Secrets and variables**, **Actions**, **New repository secret**, un par ligne du tableau.

## Google (Gmail)

1. Ouvrir <https://console.cloud.google.com/>, se connecter avec le compte Google du projet, **Sélectionner un projet**, **Nouveau projet** : nom `Quai Mail`, **Créer**.
2. Menu **API et services**, **Bibliothèque** : chercher **Gmail API**, **Activer**.
3. Menu **Google Auth Platform** (ou **Écran de consentement OAuth**) :
   - **Branding** : nom de l'application `Quai Mail`, adresse e-mail d'assistance, logo facultatif (`docs/images/logo.png`), page d'accueil `https://github.com/memton80/quai-mail`, **Enregistrer** ;
   - **Audience** : type **Externe** ; dans **Utilisateurs test**, **Ajouter des utilisateurs** : les adresses Gmail qui testeront (la sienne d'abord) ;
   - **Accès aux données** : **Ajouter ou supprimer des champs d'application**, cocher `https://mail.google.com/` (« Lire, rédiger, envoyer et supprimer définitivement tous vos e-mails »), **Mettre à jour**, **Enregistrer**.
4. **Clients** (ou **Identifiants**), **Créer un client** (ou **Créer des identifiants**, **ID client OAuth**) : type d'application **Application de bureau**, nom `Quai Mail`, **Créer**. Copier l'**ID client** et le **Code secret du client** dans les secrets GitHub ci-dessus.

**À savoir** : l'accès IMAP complet de Gmail (`https://mail.google.com/`) est un champ d'application « restreint ». Tant que l'application reste en mode **Test**, seuls les utilisateurs test ajoutés à l'étape 3 (100 au plus) peuvent se connecter, et Google leur redemande l'autorisation tous les 7 jours. Ouvrir Quai Mail à tous les utilisateurs de Gmail demande une **validation par Google** (examen de l'application et évaluation de sécurité annuelle par un organisme agréé, payante). En attendant, les utilisateurs de Gmail peuvent toujours se connecter avec un **mot de passe d'application** (déjà pris en charge).

## Microsoft (Outlook.com, Hotmail, Live, et comptes Microsoft 365)

1. Ouvrir <https://entra.microsoft.com/>, se connecter avec un compte Microsoft (un compte personnel suffit ; si le portail demande un « tenant », accepter la création gratuite d'un annuaire Microsoft Entra ID).
2. **Applications**, **Inscriptions d'applications**, **Nouvelle inscription** :
   - nom `Quai Mail` ;
   - types de comptes pris en charge : **Comptes dans un annuaire d'organisation et comptes Microsoft personnels** ;
   - URI de redirection : plateforme **Client public/natif (mobile et bureau)**, valeur `http://localhost` ;
   - **Inscrire**. Copier l'**ID d'application (client)** dans le secret `QUAI_MAIL_MICROSOFT_CLIENT_ID`.
3. **Authentification** : en bas, **Autoriser les flux clients publics** : **Oui**, **Enregistrer**.
4. **Autorisations de l'API**, **Ajouter une autorisation**, onglet **API utilisées par mon organisation**, chercher **Office 365 Exchange Online**, **Autorisations déléguées** : cocher `IMAP.AccessAsUser.All` et `SMTP.Send`, **Ajouter des autorisations**. Puis **Ajouter une autorisation**, **Microsoft Graph**, **Autorisations déléguées** : cocher `offline_access`, **Ajouter**.

**À savoir** : sans « vérification de l'éditeur » (compte Microsoft Partner), la page de connexion affiche « éditeur non vérifié » ; la connexion fonctionne quand même pour les comptes personnels. Certaines organisations (comptes professionnels) bloquent les applications non vérifiées : leur administrateur doit alors les autoriser.

## Ce que fait Quai Mail ensuite

- **Se connecter avec Google / Microsoft** dans « Ajouter un compte » ouvre le navigateur par défaut sur la page officielle de connexion (flux « code d'autorisation » avec PKCE, retour sur `http://127.0.0.1:<port>` pour Google, `http://localhost:<port>` pour Microsoft, sur l'ordinateur même seulement).
- Si l'autorisation expire ou est retirée : clic droit sur le compte, **Se reconnecter**.
- Quai Mail garde le jeton de renouvellement dans le coffre de Windows, jamais dans un fichier ni un journal ; le jeton d'accès (une heure) reste en mémoire.
- Retirer le compte, ou désinstaller avec effacement des données, efface ce jeton.
