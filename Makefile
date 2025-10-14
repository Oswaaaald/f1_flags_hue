# =========
#  Makefile
# =========

SHELL := /bin/bash

# --- Variables (override possibles : make quick GAP=0.5) ---
PY            ?= python3
APP           ?= f1_hue.py
MOCK          ?= mock_server.py
CONF_DIR      ?= ./
CONF_EXAMPLE  ?= config.example.yml
CONF          ?= $(CONF_DIR)config.yml

MOCK_SCRIPT   ?= mock_scripts/demo.yml
MOCK_PORT     ?= 8008
MOCK_SPEED    ?= 1.0
MOCK_PIDFILE  ?= .mock.pid
GAP           ?= 1.0

export LANG = C.UTF-8
export LC_ALL = C.UTF-8
export PYTHONIOENCODING = UTF-8

.PHONY: help install init-config run-live test quick \
        setup-wizard setup-link setup-lights setup-groups \
        baseline-capture baseline-restore baseline-print \
        sync-calibrate sync-show \
        mock-start mock-stop mock-restart \
        doctor web web-run

help:
	@echo ""
	@echo " F1 Hue Sync — commandes utiles"
	@echo ""
	@echo "  make install                Installer dépendances (requests, PyYAML)"
	@echo "  make init-config            Copier config.example.yml -> .f1_hue/config.yml (si absent)"
	@echo ""
	@echo "  make setup-wizard           Assistant: choisir pièce/zone(s)/lampes"
	@echo "  make setup-link             Vérifier la liaison bridge/username"
	@echo "  make setup-lights           Lister les lampes"
	@echo "  make setup-groups           Lister les groupes (pièces/zones)"
	@echo ""
	@echo "  make run-live               Lancer en live (OpenF1)"
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
	@echo "  make mock-start             Démarrer le mock (port $(MOCK_PORT), script $(MOCK_SCRIPT))"
	@echo "  make mock-stop              Stopper le mock"
	@echo "  make mock-restart           Redémarrer le mock"
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
	@echo "==> Live OpenF1"
	@$(PY) $(APP) live

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

# ---- MOCK SERVER -------------------------------------------------------------

mock-start:
	@echo "==> Démarrage mock OpenF1 (port $(MOCK_PORT), speed $(MOCK_SPEED))"
	@nohup $(PY) $(MOCK) --port $(MOCK_PORT) --script "$(MOCK_SCRIPT)" --speed $(MOCK_SPEED) > mock.log 2>&1 & echo $$! > $(MOCK_PIDFILE)
	@echo "✓ Mock lancé (PID $$(cat $(MOCK_PIDFILE))) — logs: mock.log"

mock-stop:
	@echo "==> Arrêt mock"
	@-if [ -f "$(MOCK_PIDFILE)" ]; then \
		kill $$(cat $(MOCK_PIDFILE)) 2>/dev/null || true; \
		rm -f "$(MOCK_PIDFILE)"; \
		echo "✓ Mock arrêté (PID file supprimé)"; \
	else \
		pkill -f "$(MOCK)" 2>/dev/null || true; \
		echo "• Aucun PID file — tentative pkill"; \
	fi

mock-restart: mock-stop mock-start

# ---- DOCTOR ------------------------------------------------------------------

doctor:
	@echo "==> Doctor"
	@echo " • Python      : $$($(PY) --version 2>&1)"
	@echo " • Script path : $(APP)"
	@echo " • Config path : $(CONF)  ($$( [ -f "$(CONF)" ] && echo 'existe' || echo 'absent' ))"
	@which $(PY) >/dev/null 2>&1 || (echo " ✖ python introuvable"; exit 1)
	@$(PY) -c "import sys; sys.exit(0)"
	@pip show requests >/dev/null 2>&1 && echo ' • Dep         : requests OK' || echo ' • Dep         : requests MISSING (pip install -r requirements.txt)'
	@pip show PyYAML   >/dev/null 2>&1 && echo ' • Dep         : PyYAML   OK' || echo ' • Dep         : PyYAML   MISSING (pip install -r requirements.txt)'
	@$(PY) $(APP) setup link || true
