
import unittest
class TestSanityOfNotebook(unittest.TestCase):
    def setUp(self):
        pass
    def tearDown(self):
        pass
        
    def testsanity_example(self):
        #!/usr/bin/env python
        # coding: utf-8
        
        # Model object and parameter access
        # =============================
        
        # In[1]:
        
        
        from PyAstronomy import funcFit2 as fuf2
        import matplotlib
        matplotlib.use('Agg')
        
        # Create a a model object representing a Gaussian
        gf = fuf2.GaussFit()
        
        # Get some information on the available parameters
        gf.parameterSummary()
        
        # Parameters can be accessed using the square bracket
        # notation
        gf["A"] = -10.0
        gf["sig"] = 15.77
        gf["off"] = 0.96
        gf["mu"] = 7.5
        print()
        print("Parameters values: ")
        print("  A   : ", gf["A"])
        print("  sig : ", gf["sig"])
        print("  off : ", gf["off"])
        print("  mu  : ", gf["mu"])
        print()
        
        # Wildcards can be used to specify parameters
        gf["*"] = 0.75
        gf["o?f"] = 1.0 
        
        # More convenient overview of parameter status
        gf.parameterSummary()
        print()
        
        # Exporting and assigning parameter values via dictionary
        ps = gf.parameters()
        print("Parameter name/value dictionary: ", ps)
        # Assigning values from dictionary (identical values here)
        gf.assignValues(ps)
        
