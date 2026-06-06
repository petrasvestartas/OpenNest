# pragma once

#include <stdlib.h>
#include <string>
#include <vector>
#include <unordered_map>
#include <iostream>
#include "math.h"
#include <Eigen/Core>
#include <Eigen/Dense>
#include "Eigen/Eigenvalues"


// Define EXPORT macro to handle cross-platform export
#if defined(_WIN32) || defined(_WIN64)
  #define EXPORT __declspec(dllexport)
#else
  #define EXPORT
#endif

class Vertex {
public:
    
    // location in 3d
    Eigen::Vector3d position;
    
    // id between 0 and |V|-1
    int index;
    
};

class BoundingBox {
public:
    // default constructor
    BoundingBox();
    
    // initialize with specified components
    BoundingBox(const Eigen::Vector3d& min0, const Eigen::Vector3d& max0);
    
    // initialize with specified components
    BoundingBox(const Eigen::Vector3d& p);
    
    // expand bounding box to include point/ bbox
    void expandToInclude(const Eigen::Vector3d& p);
    void expandToInclude(const BoundingBox& b);
    
    // return the max dimension
    int maxDimension() const;
    
    // check if bounding box and face intersect
    bool contains(const BoundingBox& boundingBox, double& dist) const;
    
    // computes axis aligned bounding box
    void computeAxisAlignedBox(std::vector<Vertex>& vertices);
    
    // computes oriented bounding box using principal component analysis
    void computeOrientedBox(std::vector<Vertex>& vertices);
        
    // member variables
    Eigen::Vector3d min;
    Eigen::Vector3d max;
    Eigen::Vector3d extent;
    std::vector<Eigen::Vector3d> orientedPoints;
    std::string type;
};

// External C function for computing oriented bounding box
extern "C" {
    EXPORT void pinvoke_compute_oriented_box(int pointCount, double* points, double* orientedBoxPoints);
}

