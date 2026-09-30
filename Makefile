# =========
#  Makefile
# =========

SHELL := /bin/bash

# --- Variables (override possibles : make quick GAP=0.5) ---
PY            ?= $(if $(wildcard .venv/bin/python),.venv/bin/python,python3)
APP           ?= f1_hue.py
CONF_DIR      ?= ./
CONF_EXAMPLE  ?= config.example.yml
CONF          ?= $(CONF_DIR)config.yml

GAP           ?= 1.0

export LANG = C.UTF-8
export LC_ALL = C.UTF-8
export PYTHONIOENCODING = UTF-8

.PHONY: help install init-config run-live check-live test quick \
        setup-wizard setup-link setup-lights setup-groups \
        baseline-capture baseline-restore baseline-print \
        sync-calibrate sync-show \
        doctor web web-run

help:
	@echo ""
	@echo " F1 Hue Sync — commandes utiles"
	@echo ""
	@echo "  make install                Installer les dépendances Python"
	@echo "  make init-config            Copier config.example.yml -> config.yml (si absent)"
	@echo ""
	@echo "  make setup-wizard           Assistant: choisir pièce/zone(s)/lampes"
	@echo "  make setup-link             Vérifier la liaison bridge/username"
	@echo "  make setup-lights           Lister les lampes"
	@echo "  make setup-groups           Lister les groupes (pièces/zones)"
	@echo ""
	@echo "  make run-live               Lancer en live (flux Formula 1)"
	@echo "  make check-live             Vérifier la connexion au flux sans lampes"
	@echo "  make test                   Mode test interactif"
	@echo "  make quick GAP=1.0          Démo rapide (gap entre drapeaux)"
	@echo ""
	@echo "  make sync-calibrate         Calibrer l'offset TV"
	@echo "  make sync-show              Afficher l'offset TV courant"
	@echo ""
	@echo "  make baseline-capture       Capturer la baseline utilisateur"
	@echo "  make baseline-restore       Restaurer la baseline utilisateur"
	@echo "  make baseline-print         Afficher la baseline sauvegardée"
	@echo ""
	@echo "  make doctor                 Petit check rapide (Python, deps, conf, bridge)"
	@echo ""

install:
	@echo "==> Installing requirements"
	@$(PY) -m pip install -r requirements.txt

init-config:
	@echo "==> Initialisation de la config"
	@mkdir -p "$(CONF_DIR)"
	@if [ ! -f "$(CONF)" ]; then \
		cp -n "$(CONF_EXAMPLE)" "$(CONF)"; \
		echo "✓ Copié $(CONF_EXAMPLE) -> $(CONF)"; \
	else \
		echo "• $(CONF) existe déjà (aucune action)"; \
	fi

# ---- RUN / TEST --------------------------------------------------------------

run-live:
	@echo "==> Live F1"
	@$(PY) $(APP) live

check-live:
	@$(PY) $(APP) check-live

test:
	@echo "==> Mode test"
	@$(PY) $(APP) test

quick:
	@echo "==> Démo rapide (gap=$(GAP)s)"
	@$(PY) $(APP) test --quick --gap $(GAP)

# --- Web UI --------------------------------------------------------------

web:
	@echo "==> Web UI (http://localhost:8080)"
	@$(PY) web/server.py

web-run:
	@echo "==> Web UI (http://localhost:8080)"
	@PYTHONPATH=. $(PY) web/server.py

# ---- SETUP -------------------------------------------------------------------

setup-wizard:
	@echo "==> Assistant de sélection des lampes"
	@$(PY) $(APP) setup wizard

setup-link:
	@echo "==> Vérif bridge / username"
	@$(PY) $(APP) setup link

setup-lights:
	@echo "==> Liste des lampes"
	@$(PY) $(APP) setup lights

setup-groups:
	@echo "==> Liste des groupes (pièces/zones)"
	@$(PY) $(APP) setup groups

# ---- BASELINE ----------------------------------------------------------------

baseline-capture:
	@echo "==> Capture baseline"
	@$(PY) $(APP) baseline capture

baseline-restore:
	@echo "==> Restore baseline"
	@$(PY) $(APP) baseline restore

baseline-print:
	@echo "==> Baseline actuelle"
	@$(PY) $(APP) baseline print

# ---- SYNC (offset TV) --------------------------------------------------------

sync-calibrate:
	@echo "==> Calibration offset TV"
	@$(PY) $(APP) sync calibrate

sync-show:
	@$(PY) $(APP) sync show

# ---- DOCTOR ------------------------------------------------------------------

doctor:
	@echo "==> Doctor"
	@echo " • Python      : $$($(PY) --version 2>&1)"
	@echo " • Script path : $(APP)"
	@echo " • Config path : $(CONF)  ($$( [ -f "$(CONF)" ] && echo 'existe' || echo 'absent' ))"
	@which $(PY) >/dev/null 2>&1 || (echo " ✖ python introuvable"; exit 1)
	@$(PY) -c "import sys; sys.exit(0)"
	@$(PY) -m pip show requests >/dev/null 2>&1 && echo ' • Dep         : requests OK' || echo ' • Dep         : requests MISSING (make install)'
	@$(PY) -m pip show PyYAML   >/dev/null 2>&1 && echo ' • Dep         : PyYAML   OK' || echo ' • Dep         : PyYAML   MISSING (make install)'
	@$(PY) -m pip show signalrcore >/dev/null 2>&1 && echo ' • Dep         : signalrcore OK' || echo ' • Dep         : signalrcore MISSING (make install)'
	@$(PY) $(APP) setup link || true
