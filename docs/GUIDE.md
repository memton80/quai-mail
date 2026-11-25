# Guide de Quai Mail

> **Version 0.2 : comptes et dossiers.** Ajoutez vos comptes IMAP / SMTP et parcourez leurs dossiers et la liste de vos mails. La lecture des mails arrive dans la version 0.3, l'écriture dans la 0.4.

## La fenêtre

Trois volets, de gauche à droite :

| Volet | Contenu |
|---|---|
| **Comptes et dossiers** | vos comptes mail et leurs dossiers (Boîte de réception, Envoyés…), avec le nombre de mails non lus ; en bas, **Ajouter un compte** et **Réglages** |
| **Mails** | les mails du dossier choisi, du plus récent au plus ancien : expéditeur, objet, date ; les non lus en gras |
| **Lecture** | le mail ouvert (à partir de la version 0.3), ou le brouillon en cours |

Tout se fait aussi au clavier : `Tab` passe d'un volet à l'autre, les flèches parcourent les dossiers et les mails, `F5` actualise les dossiers.

## Ajouter un compte

1. **Ajouter un compte** (en bas à gauche).
2. Saisissez votre **adresse mail** : Quai Mail cherche aussitôt les réglages du serveur :
   - pour les grands fournisseurs (Gmail, Yahoo, iCloud, Orange, Free, SFR, La Poste…), il les connaît déjà, sans rien demander à personne ;
   - sinon, il lit ceux que votre fournisseur publie lui-même sur son site (autoconfiguration) ;
   - à défaut, il propose les réglages habituels (`imap.` et `smtp.` suivis de votre domaine), à corriger si besoin dans **Réglages du serveur**.
3. Saisissez votre **mot de passe**. Gmail, Yahoo, iCloud et quelques autres demandent un **mot de passe d'application** : un mot de passe à part, créé dans les réglages de sécurité de votre compte chez eux. Quai Mail vous le signale.
4. **Ajouter** : Quai Mail se connecte pour vérifier (réception IMAP et envoi SMTP, **sans rien envoyer**). En cas d'échec, il dit pourquoi (serveur introuvable, mot de passe refusé, certificat non fiable…) et rien n'est enregistré.

Le mot de passe est gardé dans le **coffre de Windows** (Gestionnaire d'identification), jamais dans un fichier. Les connexions sont toujours chiffrées (SSL / TLS ou STARTTLS) et le certificat du serveur toujours vérifié : un certificat non fiable est refusé, sans possibilité de l'ignorer.

Outlook.com et Hotmail n'acceptent plus les mots de passe : leur connexion par le navigateur arrive dans une prochaine version.

**Retirer un compte** : clic droit sur le compte (ou l'un de ses dossiers), **Retirer ce compte de Quai Mail…**, puis confirmer. Ses réglages, son mot de passe et son cache disparaissent de cet ordinateur ; vos mails restent sur le serveur.

## Dossiers et mails

- Les dossiers particuliers (Boîte de réception, Brouillons, Envoyés, Archives, Indésirables, Corbeille) viennent en premier, puis les autres, sous-dossiers en retrait.
- La liste montre les 50 mails les plus récents ; **Afficher les mails plus anciens**, en bas de la liste, charge les 50 suivants.
- Quai Mail garde une copie des dossiers et des en-têtes (cache) pour les afficher tout de suite au démarrage, puis les met à jour depuis le serveur. Si un serveur est injoignable, un bandeau le dit et le cache reste affiché.
- Rien n'est modifié sur le serveur à ce stade : ni lu / non lu, ni déplacement, ni suppression.

## Réglages

| Réglage | Effet |
|---|---|
| **Thème** | Selon Windows, Clair ou Sombre, appliqué tout de suite |
| **Bloquer les images distantes** | activé par défaut : les images hébergées sur Internet ne sont chargées que sur votre demande, l'expéditeur ne sait pas quand vous lisez son mail |
| **Rechercher les mises à jour** | activé par défaut : au démarrage, Quai Mail demande à GitHub le numéro de la dernière version publiée |
| **Vider le cache** | efface les dossiers et en-têtes gardés sur cet ordinateur ; tout est relu sur vos serveurs, rien n'est perdu |

Les réglages s'appliquent et s'enregistrent immédiatement, sans bouton « Enregistrer ». La version installée est affichée en bas des réglages.

## Mises à jour

Quand une nouvelle version est publiée, un bandeau l'annonce en haut de la fenêtre. **Mettre à jour** :

1. télécharge l'installateur (bouton **Annuler** pendant le téléchargement) et vérifie son empreinte SHA-256 : un fichier qui ne correspond pas à celui publié est supprimé sans être exécuté ;
2. si un brouillon est en cours, demande **« Garder ce brouillon ? »** :
   - **Garder** : il est enregistré sur cet ordinateur et rouvert juste après la mise à jour ;
   - **Supprimer** : il est effacé ;
   - **Annuler** : rien ne se passe, la mise à jour attend (déjà téléchargée) que vous ayez fini ;
3. ferme Quai Mail, installe la nouvelle version sans rien demander, puis relance Quai Mail **au même endroit** : même compte, même dossier, même mail ouvert, brouillon rouvert.

Avec la version portable (zip), le bouton ouvre la page de la version sur GitHub.

## Brouillons

Un brouillon n'est jamais perdu : en fermant la fenêtre avec un brouillon en cours, Quai Mail pose la même question **« Garder ce brouillon ? »**. Si l'enregistrement échoue (disque plein, dossier protégé), Quai Mail le dit et reste ouvert. Aucun mail n'est jamais envoyé sans que vous cliquiez sur **Envoyer**.

## Vos données

- Réglages : `%LOCALAPPDATA%\QuaiMail\settings.json` (thème, images distantes, mises à jour).
- Comptes : `%LOCALAPPDATA%\QuaiMail\accounts.json` (adresse, serveurs, identifiant ; **aucun mot de passe**). Mots de passe : coffre de Windows (Gestionnaire d'identification, « QuaiMail »).
- Cache : `%LOCALAPPDATA%\QuaiMail\cache\mail.db` (dossiers et en-têtes), effaçable à tout moment.
- Brouillons gardés : `%LOCALAPPDATA%\QuaiMail\drafts`, un fichier par brouillon.
- Journaux : `%LOCALAPPDATA%\QuaiMail\logs` (7 jours). Jamais de mot de passe, d'objet, de contenu ni d'adresse de correspondant.
- Mise à jour en cours : `%LOCALAPPDATA%\QuaiMail\updates` (installateur, effacé au démarrage suivant ; `reopen.txt`, l'état de la fenêtre à restaurer, effacé dès qu'il a servi).
- Désinstallation : depuis « Applications installées » de Windows. Elle propose aussi d'effacer vos comptes, mots de passe (coffre de Windows), brouillons, cache, réglages et journaux ; vos mails restent sur vos serveurs.

## Connexions

Quai Mail ne se connecte qu'aux serveurs mail de vos comptes, à GitHub pour savoir s'il existe une nouvelle version (rien sur vous ni sur vos mails ; désactivable dans les **Réglages**) et, au moment d'ajouter un compte, au site de votre fournisseur pour lire ses réglages publiés (seul le nom de domaine apparaît dans l'adresse demandée, jamais votre adresse mail). Aucune publicité, aucun pistage, aucun compte à créer.

## Limites actuelles

Version 0.2 : on voit les dossiers et la liste des mails, mais pas encore leur contenu (version 0.3), ni l'écriture (0.4), ni la recherche, le déplacement, la suppression ou la réception en direct (0.5), ni Gmail / Outlook par le navigateur (0.6).
