# Sky data: licences and credits

These files are published as assets of the ExoInstruments `sky-data` releases and installed by
`tools/get_sky_data_compact.py`, `tools/get_sky_data_complete.py` and `tools/setup_data.py`. They
are derived from third-party surveys. The ExoInstruments licence does not apply to them: each is
available under the terms below, for non-commercial use only. No data provider endorses
ExoInstruments.

## Gaia DR3 star catalogues

Files: `GaiaStarCatalog.starcat`, `GaiaStarCatalog.V13.starcat`, `GaiaStarCatalog.V15.starcat`,
`GaiaStarCatalog.V17.starcat`, `GaiaStarCatalog.V19.starcat`.

- Source: Gaia DR3 `gaia_source` (https://doi.org/10.5270/esa-qa4lep3). Credit: ESA, Gaia DPAC.
- Licence: CC BY-NC 3.0 IGO, https://creativecommons.org/licenses/by-nc/3.0/igo/
- Changes: positions re-encoded in fixed point; G and BP-RP converted to Johnson V and B-V with the
  relations of the Gaia DR3 documentation (Table 5.9); E(B-V) taken as ag_gspphot / 3.1; proper
  motions, parallaxes and identifiers dropped. The V13 to V19 files keep only the stars at or
  brighter than that V magnitude.
- Acknowledgement: "This work has made use of data from the European Space Agency (ESA) mission
  Gaia (https://www.cosmos.esa.int/gaia), processed by the Gaia Data Processing and Analysis
  Consortium (DPAC, https://www.cosmos.esa.int/web/gaia/dpac/consortium). Funding for the DPAC has
  been provided by national institutions, in particular the institutions participating in the Gaia
  Multilateral Agreement."
- References: Gaia Collaboration, Prusti et al. 2016, A&A 595, A1. Gaia Collaboration, Vallenari
  et al. 2023, A&A 674, A1.

## Dust map

File: `DustMap.dustmap`.

- Source: SFD 1998 Map of Galactic Dust, Finkbeiner, Schlegel & Davis, Harvard Dataverse,
  https://doi.org/10.7910/DVN/EWCNL5. Licence: CC0 1.0.
- Changes: E(B-V) sampled at the centres of HEALPix nside 1024 pixels, scaled by 0.86, stored as
  half precision.
- References: Schlegel, Finkbeiner & Davis 1998, ApJ 500, 525. Schlafly & Finkbeiner 2011, ApJ
  737, 103.

## Galaxy catalogue

File: `GalaxyCatalog.galcat`.

- Source: HyperLEDA (http://leda.univ-lyon1.fr), mean data for galaxies with B_T at or brighter than
  15, which include RC3. Terms: publicly available for non-commercial purposes.
- Changes: position, B_T, B-V, D25, axis ratio, position angle, morphological type and distance
  modulus converted into one record per galaxy; incomplete rows dropped.
- Acknowledgement: "We acknowledge the usage of the HyperLeda database (http://leda.univ-lyon1.fr)."
- References: Makarov et al. 2014, A&A 570, A13. de Vaucouleurs et al. 1991, Third Reference
  Catalogue of Bright Galaxies.

## Galaxy images

File: `GalaxyImages.galimg`, shape maps of the brightest galaxies.

- Licences: CC BY 4.0 (Legacy Surveys), ODbL 1.0 (CDS HiPS) and each survey's terms; non-commercial use.
- Changes: survey cutouts reprojected onto a tangent plane, residual sky removed, faint pixels
  blended with a model profile, clipped, normalised to a total of one and stored as half
  precision. Photometric calibration is discarded. Galaxy names and positions come from HyperLEDA.
- Cutouts were made with hips2fits, a service provided by CDS
  (https://alasky.cds.unistra.fr/hips-image-services/hips2fits), except the Pan-STARRS stack
  cutouts, which come from STScI. The CDS HiPS are copyright Université de Strasbourg/CNRS. This
  file contains information from the HiPS listed below, which is made available here under the
  Open Database License (ODbL 1.0, https://opendatacommons.org/licenses/odbl/1-0/).

DESI Legacy Imaging Surveys DR10, g and r.
- Credit: Legacy Surveys / D. Lang (Perimeter Institute). Licence: CC BY 4.0,
  https://creativecommons.org/licenses/by/4.0/. Acknowledgement:
  https://www.legacysurvey.org/acknowledgment/
- HiPS: CDS/P/DESI-Legacy-Surveys/DR10/g (doi:10.26093/cds/aladin/296y-bgk) and
  CDS/P/DESI-Legacy-Surveys/DR10/r (doi:10.26093/cds/aladin/3zv5-jqr). Reference: Dey et al. 2019,
  AJ 157, 168.

Pan-STARRS1 DR1, g and r.
- Data from the PS1 surveys, used under the licence STScI grants to use, reproduce and publicly
  display them, retrieved from the Mikulski Archive for Space Telescopes (MAST) at STScI
  (https://panstarrs.stsci.edu/). HiPS: CDS/P/PanSTARRS/DR1/g.
- Acknowledgement: "The Pan-STARRS1 Surveys (PS1) and the PS1 public science archive have been made
  possible through contributions by the Institute for Astronomy, the University of Hawaii, the
  Pan-STARRS Project Office, the Max-Planck Society and its participating institutes, the Max
  Planck Institute for Astronomy, Heidelberg and the Max Planck Institute for Extraterrestrial
  Physics, Garching, The Johns Hopkins University, Durham University, the University of Edinburgh,
  the Queen's University Belfast, the Harvard-Smithsonian Center for Astrophysics, the Las Cumbres
  Observatory Global Telescope Network Incorporated, the National Central University of Taiwan, the
  Space Telescope Science Institute, the National Aeronautics and Space Administration under Grant
  No. NNX08AR22G issued through the Planetary Science Division of the NASA Science Mission
  Directorate, the National Science Foundation Grant No. AST-1238877, the University of Maryland,
  Eotvos Lorand University (ELTE), the Los Alamos National Laboratory, and the Gordon and Betty
  Moore Foundation."
- Reference: Chambers et al. 2016, arXiv:1612.05560.

SDSS DR9, g, r, i and z.
- Credit: Sloan Digital Sky Survey (https://www.sdss.org/). Non-commercial use. HiPS: CDS/P/SDSS9.
- Acknowledgement: "Funding for SDSS-III has been provided by the Alfred P. Sloan Foundation, the
  Participating Institutions, the National Science Foundation, and the U.S. Department of Energy
  Office of Science. The SDSS-III web site is http://www.sdss3.org/. SDSS-III is managed by the
  Astrophysical Research Consortium for the Participating Institutions of the SDSS-III
  Collaboration including the University of Arizona, the Brazilian Participation Group, Brookhaven
  National Laboratory, Carnegie Mellon University, University of Florida, the French Participation
  Group, the German Participation Group, Harvard University, the Instituto de Astrofisica de
  Canarias, the Michigan State/Notre Dame/JINA Participation Group, Johns Hopkins University,
  Lawrence Berkeley National Laboratory, Max Planck Institute for Astrophysics, Max Planck Institute
  for Extraterrestrial Physics, New Mexico State University, New York University, Ohio State
  University, Pennsylvania State University, University of Portsmouth, Princeton University, the
  Spanish Participation Group, University of Tokyo, University of Utah, Vanderbilt University,
  University of Virginia, University of Washington, and Yale University."
- Reference: Ahn et al. 2012, ApJS 203, 21.

Dark Energy Survey DR2, g and r.
- Credit: The Dark Energy Survey Data Release 2. Non-commercial use. HiPS: CDS/P/DES-DR2/g
  (doi:10.26093/cds/aladin/ph6k-ye) and CDS/P/DES-DR2/r (doi:10.26093/cds/aladin/10cx-as5).
- Acknowledgement: "This project used public archival data from the Dark Energy Survey (DES).
  Funding for the DES Projects has been provided by the U.S. Department of Energy, the U.S.
  National Science Foundation, the Ministry of Science and Education of Spain, the Science and
  Technology Facilities Council of the United Kingdom, the Higher Education Funding Council for
  England, the National Center for Supercomputing Applications at the University of Illinois at
  Urbana-Champaign, the Kavli Institute of Cosmological Physics at the University of Chicago, the
  Center for Cosmology and Astro-Particle Physics at the Ohio State University, the Mitchell
  Institute for Fundamental Physics and Astronomy at Texas A&M University, Financiadora de Estudos
  e Projetos, Fundação Carlos Chagas Filho de Amparo à Pesquisa do Estado do Rio de Janeiro,
  Conselho Nacional de Desenvolvimento Científico e Tecnológico and the Ministério da Ciência,
  Tecnologia e Inovação, the Deutsche Forschungsgemeinschaft and the Collaborating Institutions in
  the Dark Energy Survey. The Collaborating Institutions are Argonne National Laboratory, the
  University of California at Santa Cruz, the University of Cambridge, Centro de Investigaciones
  Enérgeticas, Medioambientales y Tecnológicas-Madrid, the University of Chicago, University College
  London, the DES-Brazil Consortium, the University of Edinburgh, the Eidgenössische Technische
  Hochschule (ETH) Zürich, Fermi National Accelerator Laboratory, the University of Illinois at
  Urbana-Champaign, the Institut de Ciències de l'Espai (IEEC/CSIC), the Institut de Física d'Altes
  Energies, Lawrence Berkeley National Laboratory, the Ludwig-Maximilians Universität München and
  the associated Excellence Cluster Universe, the University of Michigan, the National Optical
  Astronomy Observatory, the University of Nottingham, The Ohio State University, the OzDES
  Membership Consortium, the University of Pennsylvania, the University of Portsmouth, SLAC National
  Accelerator Laboratory, Stanford University, the University of Sussex, and Texas A&M University.
  Based in part on observations at Cerro Tololo Inter-American Observatory, National Optical
  Astronomy Observatory, which is operated by the Association of Universities for Research in
  Astronomy (AURA) under a cooperative agreement with the National Science Foundation."
- Reference: DES Collaboration, Abbott et al. 2021, ApJS 255, 20.

## Built on your computer instead

`HalphaMap.emission` and `HalphaPatches.patchset` are not in these releases. `tools/setup_data.py`
builds them from NASA LAMBDA, SHASSA and NSNS, because the terms of SHASSA and VTSS, two inputs of
the Finkbeiner (2003) H-alpha composite, do not grant redistribution.
