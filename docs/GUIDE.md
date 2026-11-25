# Guide de Quai Mail

> **Version 0.1 : la coquille.** Cette première version pose les fondations : l'installation, la fenêtre et la mise à jour automatique. Les comptes mail arrivent dans la version 0.2, la lecture des mails dans la 0.3, l'écriture dans la 0.4.

## La fenêtre

Trois volets, de gauche à droite :

| Volet | Contenu |
|---|---|
| **Comptes et dossiers** | vos comptes mail et leurs dossiers (Boîte de réception, Envoyés…) ; le bouton **Réglages** est en bas |
| **Mails** | les mails du dossier choisi |
| **Lecture** | le mail ouvert, ou le brouillon en cours |

Tout se fait aussi au clavier : `Tab` passe d'un élément à l'autre, `Entrée` ou `Espace` active un bouton.

## Réglages

| Réglage | Effet |
|---|---|
| **Thème** | Selon Windows, Clair ou Sombre, appliqué tout de suite |
| **Bloquer les images distantes** | activé par défaut : les images hébergées sur Internet ne sont chargées que sur votre demande, l'expéditeur ne sait pas quand vous lisez son mail |
| **Rechercher les mises à jour** | activé par défaut : au démarrage, Quai Mail demande à GitHub le numéro de la dernière version publiée |

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
- Brouillons gardés : `%LOCALAPPDATA%\QuaiMail\drafts`, un fichier par brouillon.
- Journaux : `%LOCALAPPDATA%\QuaiMail\logs` (7 jours). Jamais de mot de passe, d'objet, de contenu ni d'adresse de correspondant.
- Mise à jour en cours : `%LOCALAPPDATA%\QuaiMail\updates` (installateur, effacé au démarrage suivant ; `reopen.txt`, l'état de la fenêtre à restaurer, effacé dès qu'il a servi).
- Désinstallation : depuis « Applications installées » de Windows. Elle propose aussi d'effacer vos réglages, brouillons et journaux ; vos mails restent sur vos serveurs.

## Connexions

Quai Mail ne se connecte qu'à GitHub, pour savoir s'il existe une nouvelle version (rien sur vous ni sur vos mails ; désactivable dans les **Réglages**), et, à partir de la version 0.2, aux serveurs mail que vous aurez configurés. Aucune publicité, aucun pistage, aucun compte à créer.

## Limites actuelles

Version 0.1 : pas encore de compte, de lecture ni d'écriture de mails. Prochaine étape : les comptes IMAP / SMTP et leurs dossiers.
