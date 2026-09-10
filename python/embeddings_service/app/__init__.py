"""Service HTTP d'embeddings et d'index vectoriel pour AssistantQR.

Ce paquet est volontairement minuscule : il n'expose que ce dont le cote C# a
besoin (contrat de la section 7 du contrat technique). Aucun framework RAG,
aucune chaine d'agents : le seul travail de ce service est de transformer du
texte en vecteurs et de retrouver les k plus proches.
"""

__all__ = ["__version__"]

__version__ = "1.0.0"
