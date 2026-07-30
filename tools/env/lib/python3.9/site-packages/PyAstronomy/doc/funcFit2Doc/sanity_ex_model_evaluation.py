
import unittest
class TestSanityOfNotebook(unittest.TestCase):
    def setUp(self):
        pass
    def tearDown(self):
        pass
        
    def testsanity_example(self):
        #!/usr/bin/env python
        # coding: utf-8
        
        # Model evaluation
        # =============================
        
        # In[1]:
        
        
        from PyAstronomy import funcFit2 as fuf2
        import numpy as np
        import matplotlib.pylab as plt
        import matplotlib
        matplotlib.use('Agg')
        
        # Create a a model object representing a Gaussian
        gf = fuf2.GaussFit()
        
        # Parameters can be accessed using the square bracket
        # notation
        gf["A"] = 10.0
        gf["sig"] = 15.77
        gf["off"] = 1.0
        gf["mu"] = 7.5
        
        x = np.linspace(gf["mu"]-5*gf["sig"], gf["mu"]+5*gf["sig"], 150)
        y = gf.evaluate(x)
        
        gf["A"] = 7.5
        y2 = gf.evaluate(x)
        
        plt.plot(x, y, 'b.-', label="A=10")
        plt.plot(x, y2, 'r.-', label="A=7.5")
        plt.legend()
        plt.show()
        
        # In[2]:
        
        
        
        
