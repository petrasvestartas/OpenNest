#include <iostream>
#include <vector>
#include "boundingbox.h"  // Include the header file for Vertex and BoundingBox

int main() {
    // Create some vertices
    std::vector<Vertex> vertices = {
        {Eigen::Vector3d(1.0, 2.0, 3.0)},
        {Eigen::Vector3d(4.0, 5.0, 6.0)},
        {Eigen::Vector3d(7.0, 8.0, 9.0)},
        {Eigen::Vector3d(10.0, 11.0, 12.0)}
    };

    // Create a BoundingBox object
    BoundingBox bbox;

    // Compute the oriented bounding box
    bbox.computeOrientedBox(vertices);

    // Output the results
    std::cout << "Bounding Box Type: " << bbox.type << std::endl;
    std::cout << "Oriented Points:" << std::endl;
    for (const auto& point : bbox.orientedPoints) {
        std::cout << point.transpose() << std::endl;
    }

    return 0;
}