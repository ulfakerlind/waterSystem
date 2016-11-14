# waterSystem
This project is designed for a raspberry pi.

This project solves the problem of watering ones plants at home while one is traveling or extremely busy. Goals of this project is to produce a system that requires as little calibration as possible. Through a local website, one can inform the system what the user considers to be the minimum moisture level and, shortly after watering the plant, what the user considers to be the maximum moisture level. After this point, the sensor that measures some quantitiy should not drift.

After attempting to run tests with the moisture sensor, it became apperent after two weeks that the moisture sensors I have tried so far cannot be left in moist soil... A work-around solution is to adapt this project to only measure the weight of a plant in a pot via a load cell. Load cells are according to many website the most reliable type of sensor, and they do not loose their rigidness with time.

Furthermore this project uses the Clayster.Library.RaspberryPi library which can be downloaded here on github.
