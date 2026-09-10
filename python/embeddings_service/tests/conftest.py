"""Outillage commun des tests.

Le paquet `app` vit un cran au-dessus de `tests/`. On l'ajoute explicitement au
chemin d'import plutot que de dependre de la facon dont pytest devine sa racine :
un test qui ne se lance que depuis le bon repertoire est un test qui ne se lance
pas.
"""

from __future__ import annotations

import sys
from pathlib import Path

SERVICE_ROOT = Path(__file__).resolve().parent.parent
if str(SERVICE_ROOT) not in sys.path:
    sys.path.insert(0, str(SERVICE_ROOT))
