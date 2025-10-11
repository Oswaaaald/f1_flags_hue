# F1 Hue Sync

Synchronise vos lampes **Philips Hue** avec les **drapeaux F1**  en temps réel (OpenF1).  
Clignotements réguliers (`alert`), **switch instantané**, fondu propre, **baseline** utilisateur, **offset TV** + **calibration**, et **serveur mock** pour tester hors GP.

> Non affilié à Formula 1®, F1 TV ou Signify/Philips Hue.

---

## 🚀 TL;DR

Installez les dépendances :
```bash
pip install -r requirements.txt
```

Une fois configuré, lancez simplement :
```bash
make run-live
```

Calibrez votre TV avec les drapeaux F1 :
```bash
make sync-calibrate
```