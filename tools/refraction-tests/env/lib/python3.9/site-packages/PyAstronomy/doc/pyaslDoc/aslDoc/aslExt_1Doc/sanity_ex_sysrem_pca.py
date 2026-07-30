
import unittest
class TestSanityOfNotebook(unittest.TestCase):
    def setUp(self):
        pass
    def tearDown(self):
        pass
        
    def testsanity_example(self):
        #!/usr/bin/env python
        # coding: utf-8
        
        # SYSREM, Principle Component Analysis, and eigenvectors
        # ===================================================
        
        # A comparison between the results of SYSREM, a PCA, and the eigenvectors of a small artificial data matrix, using equal uncertainties for all data points. In this case, all of these are basically equivalent.
        
        # In[1]:
        
        
        from PyAstronomy import pyasl
        import numpy as np
        from sklearn.decomposition import PCA
        import matplotlib
        matplotlib.use('Agg')
        np.set_printoptions(precision=4)
        
        # In[2]:
        
        
        # n observations (e.g., light curves) with m data points each
        n = 4
        m = 7
        
        # Some arbitrary observations (with observations is COLUMNS)
        obs = np.zeros( (m,n) )
        for i in range(0, n):
            for j in range(m):
                obs[j,i] = j*i**3+j*i**2+(j+i+1)
        # Equal error for all data points
        sigs = np.ones_like(obs)
        
        print("Observations")
        print(obs)
        
        # *PCA*
        
        # In[3]:
        
        
        print("PCA analysis with sklearn")
        pca = PCA()
        # Use transpose to arrange observations along rows
        res = pca.fit(obs.T)
        print(f"PCA components:")
        print(pca.components_)
        print(f"N features = {pca.n_features_in_}")
        print(f"N samples = {pca.n_samples_}")
        print(f"Means = {pca.mean_}")
        
        # *Eigenvalues and -vectors (of covarinace matrix)*
        
        # In[4]:
        
        
        print("Centering data matrix")
        obscentered = obs.copy()
        for i in range(obs.shape[0]):
            obscentered[i,::] -= np.mean(obscentered[i,::])
        
        # Covariance matrix
        covm = np.matmul(obscentered, obscentered.T) / (n-1)
        V, W = np.linalg.eig(covm)
        V = np.abs(V)
        print(f"Eigenvalues = {np.array(sorted(V, reverse=True))}")
        indi = np.argsort(V)
        print("Eigenvalue and corresponding eigenvector")
        for i in range(2):
            print("    %g " % V[indi[-1-i]], np.real(W[::,indi[-1-i]]))
        
        # *SYSREM*
        # 
        # The unit length vectors 'a' of the SYSREM model may be compared with the PCA components and the eigenvectors pertaining to the largest two eigenvalues. They are equal except for, potentially, a sign. 
        
        # In[5]:
        
        
        # Instatiate SYSREM object. Apply centering across fetaures but not along observations 
        sr = pyasl.SysRem(obs, sigs, ms_obs=False, ms_feat=True)
        print("First SYSREM iteration")
        r1, a1, c1 = sr.iterate()
        print("unit vector a = ", a1/np.linalg.norm(a1))
        print("last_ac_iterations: ", sr.last_ac_iterations)
        print("Second SYSREM iteration")
        r2, a2, c2 = sr.iterate()
        print("unit vector a = ", a2/np.linalg.norm(a2))
        print("last_ac_iterations: ", sr.last_ac_iterations)
        
        
